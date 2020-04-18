namespace Ahghee

open Google.Protobuf
open Google.Protobuf.Collections
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Data.SqlTypes
open System.Diagnostics
open System.IO
open System.Linq
open System.Net.NetworkInformation
open System.Threading
open System.Threading.Tasks
open Ahghee.Grpc

type ClusterServices() = 
    let remotePartitions = new ConcurrentDictionary<int,FileStorePartition>()
    member this.RemotePartitions() = remotePartitions
    interface IClusterServices with 
        member this.RemoteLookup (partition:int) (hash:NodeIdHash) : bool * MemoryPointer = 
            if remotePartitions.ContainsKey partition then 
                let remote = remotePartitions.[ partition ]
                let mutable refPointers :Pointers = null
                let rind = remote.Index()
                if rind.TryGetValue(hash, & refPointers) then
                    true, refPointers.Pointers_ |> Seq.head
                else
                    false, Utils.NullMemoryPointer()
                
            else false, Utils.NullMemoryPointer()    
            


type GrpcFileStore(config:Config) = 

    let clusterServices = new ClusterServices()

    let PartitionWriters = 
        let bcs = 
            seq {for i in 0 .. (config.ParitionCount - 1) do 
                 yield i}
            |> Array.ofSeq
        let writers = 
            bcs    
            |>  Seq.map (fun (i) -> 
                    
                let partition = FileStorePartition(config,i,clusterServices)   
                
                (partition.IORequests(), partition.Thread(), partition)
                )            
            |> Array.ofSeq
        
        for i in 0 .. (writers.Length - 1) do
            let (_,_,part) = writers.[i]
            clusterServices.RemotePartitions().AddOrUpdate(i,part, (fun x p -> part)) |> ignore
            
            
        writers                     
    let mergeNodesById (node:Node[]) =
        node
        |> Seq.groupBy(fun n -> n.Id)
        |> Seq.map(fun (m1,m2) -> m2 |> Seq.reduce(fun i1 i2 ->
                                                        i1.MergeFrom(i2)
                                                        let noDuplicates = i1.Attributes.Distinct().ToList()
                                                        i1.Attributes.Clear()
                                                        i1.Attributes.AddRange(noDuplicates)
                                                        i1))
    
    let setTimestamps (node:Node) (nowInt:Int64) =
        for kv in node.Attributes do
            kv.Key.Timestamp <- nowInt
            kv.Value.Timestamp <- nowInt
    
    let Flush () =
        let parentTask = Task.Factory.StartNew((fun () ->
            let allDone =
                seq {for (bc,t,_) in PartitionWriters do
                        let fwtcs = new TaskCompletionSource<unit>(TaskCreationOptions.AttachedToParent)
                        while bc.Writer.TryWrite ( FlushAdds(fwtcs)) = false do ()
                        let tcs = new TaskCompletionSource<unit>(TaskCreationOptions.AttachedToParent)
                        while bc.Writer.TryWrite ( FlushFixPointers(tcs)) = false do ()
                        let ffltcs = new TaskCompletionSource<unit>(TaskCreationOptions.AttachedToParent)
                        while bc.Writer.TryWrite ( FlushFragmentLinks(ffltcs)) = false do ()
                        yield [ fwtcs.Task :> Task; tcs.Task :> Task; ffltcs.Task :> Task]}
                |> Seq.collect (fun x -> x)
                |> List.ofSeq // force it to run
            allDone    
            ))
        parentTask.Wait()        
        ()
    
    let DataBlockCMP (left:DataBlock, op:String, right:DataBlock) =
        match op with
            | "==" -> left = right
            | "<" -> left < right
            | ">" -> left > right
            | "<=" -> left <= right
            | ">=" -> left >= right
            | _ -> raise <| Exception (sprintf "Operation not supported op %s" op)
    
    let rec FilterNode (node:Node, cmp: FilterOperator.Types.Compare) =
        match cmp.CmpTypeCase with
            | FilterOperator.Types.Compare.CmpTypeOneofCase.KevValueCmp ->
                node.Attributes
                    |> Seq.exists (fun kv ->
                                    kv.Key.Data = cmp.KevValueCmp.Property
                                        && DataBlockCMP (kv.Value.Data, cmp.KevValueCmp.MATHOP, kv.Value.Data)
                                    )
            | FilterOperator.Types.Compare.CmpTypeOneofCase.CompoundCmp ->
                match cmp.CompoundCmp.BOOLOP with
                    | "&&" -> FilterNode(node, cmp.CompoundCmp.Left) && FilterNode(node, cmp.CompoundCmp.Right)
                    | "||" -> FilterNode(node, cmp.CompoundCmp.Left) || FilterNode(node, cmp.CompoundCmp.Right)
                    | _ -> raise <| Exception (sprintf "Operation not supported op %s" cmp.CompoundCmp.BOOLOP)
            | FilterOperator.Types.Compare.CmpTypeOneofCase.None -> true // ignore
            | _ -> true // shouldn't happen
    
    let rec EdgeCmp (dataBlock:DataBlock, cmp: FollowOperator.Types.EdgeNum) =
        match cmp.OpCase with
            | FollowOperator.Types.EdgeNum.OpOneofCase.EdgeRange -> 
                dataBlock = cmp.EdgeRange.Edge
            | FollowOperator.Types.EdgeNum.OpOneofCase.EdgeCmp ->
                match cmp.EdgeCmp.BOOLOP with
                    | "&&" -> EdgeCmp(dataBlock, cmp.EdgeCmp.Left) && EdgeCmp(dataBlock, cmp.EdgeCmp.Right)
                    | "||" -> EdgeCmp(dataBlock, cmp.EdgeCmp.Left) || EdgeCmp(dataBlock, cmp.EdgeCmp.Right)
                    | _ -> raise <| Exception (sprintf "Operation not supported op %s" cmp.EdgeCmp.BOOLOP)
            | _ -> false
    
    let rec EdgeCmpDecr(edgeNum: FollowOperator.Types.EdgeNum) =
        match edgeNum.OpCase with
            | FollowOperator.Types.EdgeNum.OpOneofCase.EdgeRange -> 
                edgeNum.EdgeRange.Range.To <- edgeNum.EdgeRange.Range.To - 1
            | FollowOperator.Types.EdgeNum.OpOneofCase.EdgeCmp ->
                EdgeCmpDecr (edgeNum.EdgeCmp.Left)
                EdgeCmpDecr (edgeNum.EdgeCmp.Right)
            | _ -> ()
    
    let rec EdgeCmpValid(edgeNum: FollowOperator.Types.EdgeNum) =
        match edgeNum.OpCase with
            | FollowOperator.Types.EdgeNum.OpOneofCase.EdgeRange -> 
                edgeNum.EdgeRange.Range.To > 0
            | FollowOperator.Types.EdgeNum.OpOneofCase.EdgeCmp ->
                EdgeCmpValid (edgeNum.EdgeCmp.Left) &&
                    EdgeCmpValid (edgeNum.EdgeCmp.Right)
            | _ -> false
    
    let rec MergeSameSteps(step:Step)=
        match step.Next with
            | null -> step
            | next when next.OperatorCase <> step.OperatorCase -> step
            | next when step.OperatorCase = Step.OperatorOneofCase.None -> step
            | next when step.OperatorCase = Step.OperatorOneofCase.Where ->
                let andedFilter = new Step()
                andedFilter.Where <- new FilterOperator()
                andedFilter.Where.Compare <- new FilterOperator.Types.Compare()
                andedFilter.Where.Compare.CompoundCmp <- new FilterOperator.Types.CompareCompound()
                andedFilter.Where.Compare.CompoundCmp.BOOLOP <- "&&"
                andedFilter.Where.Compare.CompoundCmp.Left <- step.Where.Compare
                andedFilter.Where.Compare.CompoundCmp.Right <- next.Where.Compare
                andedFilter.Next <- next.Next
                MergeSameSteps andedFilter
            | next when step.OperatorCase = Step.OperatorOneofCase.Follow ->
                match step.Follow.FollowCase, next.Follow.FollowCase with
                    | (_, FollowOperator.FollowOneofCase.FollowAny) ->
                        // any and any is still any, just skip this one.
                        MergeSameSteps next 
                    | (FollowOperator.FollowOneofCase.FollowAny, FollowOperator.FollowOneofCase.FollowEdge) ->
                        // any and an edge, is still and any, skip the next one
                        step.Next <- next.Next
                        MergeSameSteps step
                    | (FollowOperator.FollowOneofCase.FollowEdge, FollowOperator.FollowOneofCase.FollowEdge) ->
                        let andedFilter = new Step()
                        andedFilter.Follow <- new FollowOperator()
                        andedFilter.Follow.FollowEdge <- new FollowOperator.Types.EdgeNum()
                        andedFilter.Follow.FollowEdge.EdgeCmp <- new FollowOperator.Types.EdgeCMP()
                        andedFilter.Follow.FollowEdge.EdgeCmp.BOOLOP <- "&&"
                        andedFilter.Follow.FollowEdge.EdgeCmp.Left <- step.Follow.FollowEdge
                        andedFilter.Follow.FollowEdge.EdgeCmp.Left <- next.Follow.FollowEdge
                        andedFilter.Next <- next.Next
                        MergeSameSteps andedFilter
                    | (_,_) -> step
            | _ -> step
                
                
                
            
    
    let rec QueryNodes(addressBlock:seq<NodeID>, step: Step) : System.Threading.Tasks.Task<seq<struct(NodeID * Either<Node, Exception>)>> =
        // a where(filter) and then a follow can be handled in the same iteration, though
        // not true for the inverse
        // additionally multiple where filters in a sequence all need to be merged as ANDed
        // merge with next step, if next step is same as this step using AND logic
        
        // TODO: during recursion, no need to call MergeSameSteps, as its already happened on a previous call stack 
        let fixedStep =
            if step <> null then
                MergeSameSteps step
            else
                Step()
        
        let requestsMade =
            addressBlock
            |> Seq.map (fun ab ->
                let tcs = TaskCompletionSource<Node[]>()
                let nid = ab
                let nodeHash = Utils.GetAddressBlockHash ab
                let partition = Utils.GetPartitionFromHash config.ParitionCount nodeHash
                // this line is just plain wrong, we don't have a pointer with any of this data here.
                // if we did, then this would be ok to go I think.
                // Console.WriteLine("About to query shard "+ partition.ToString())
                let (bc,t,part) = PartitionWriters.[int <| partition]
                
                // TODO: Read all the fragments, not just the first one.
                let t = 
                    if (nid.Pointer = Utils.NullMemoryPointer()) then
                        let mutable mp:Pointers = null
                        if(part.Index().TryGetValue(nodeHash, &mp)) then 
                            while bc.Writer.TryWrite (Read(tcs, mp.Pointers_ |> Array.ofSeq)) = false do ()
                            tcs.Task
                        else 
                            tcs.SetException(new KeyNotFoundException("Index of NodeID -> MemoryPointer: did not contain the NodeID")) 
                            tcs.Task   
                    else 
                        while bc.Writer.TryWrite (Read(tcs, [|nid.Pointer|])) = false do ()
                        tcs.Task
                        
                let res = t.ContinueWith(fun (isdone:Task<Node[]>) ->
                            if (isdone.IsCompletedSuccessfully) then
                                config.Metrics.Measure.Meter.Mark(Metrics.FileStoreMetrics.ItemFragmentMeter)
                                ab,Left(isdone.Result)
                            else 
                                ab,Right(isdone.Exception :> Exception)
                            )
                res)
            
        Task.FromResult 
            (seq {
                use itemTimer = config.Metrics.Measure.Timer.Time(Metrics.FileStoreMetrics.ItemTimer)
                let nextLevel: List<NodeID> = List<NodeID>()
                let stepIsFilter = fixedStep.OperatorCase = Step.OperatorOneofCase.Where
                let nextIsFollow = fixedStep.Next <> null && fixedStep.Next.OperatorCase = Step.OperatorOneofCase.Follow
                let thisIsFollow = fixedStep <> null && fixedStep.OperatorCase = Step.OperatorOneofCase.Follow
                let follow =
                            if thisIsFollow then
                                Some( fixedStep.Follow )
                            else if nextIsFollow then
                                Some (fixedStep.Next.Follow)
                            else
                                None
                for ts in requestsMade do
                    let (ab,eith) = ts.Result

                    let (toyield,matched) = 
                        match eith with 
                        | Left(nodes) ->
                            let node =
                                nodes |> Array.reduce(fun n1 n2 ->
                                                           n1.MergeFrom(n2)
                                                           n1)
                                
                              // Handle Follow Operator here.
                            match follow with
                            | None -> ()
                            | Some(f) ->
                                node.Attributes
                                    |> Seq.iter (fun a ->
                                        if a.Value.Data.DataCase = DataBlock.DataOneofCase.Nodeid then
                                            match f.FollowCase with
                                                | FollowOperator.FollowOneofCase.FollowAny ->
                                                    nextLevel.Add(a.Value.Data.Nodeid)
                                                    ()
                                                | FollowOperator.FollowOneofCase.FollowEdge ->
                                                    if EdgeCmp( a.Key.Data, f.FollowEdge) then
                                                        nextLevel.Add(a.Value.Data.Nodeid)
                                                    ()
                                                | _ -> ()
                                        ()
                                        )
                            
                            let _matched = (not stepIsFilter) || FilterNode(node, fixedStep.Where.Compare)
                                
                            struct (ab,Left(node)), _matched
                        | Right(err) -> (ab,Right(err)), true
                       
                    if matched then
                        yield toyield
                    ()
                
                // update Step if it has recursive stuff in it like our follow operator does. Will need to decrement each follow limit.
                // if any ANDed follow limit is zero then we abort I think.
                if thisIsFollow then
                    let keepGoing =
                        match fixedStep.Follow.FollowCase with
                        | FollowOperator.FollowOneofCase.FollowAny ->
                            fixedStep.Follow.FollowAny.Range.To <- fixedStep.Follow.FollowAny.Range.To - 1 
                            fixedStep.Follow.FollowAny.Range.To > 0
                        | FollowOperator.FollowOneofCase.FollowEdge ->
                            EdgeCmpDecr ( fixedStep.Follow.FollowEdge )
                            EdgeCmpValid ( fixedStep.Follow.FollowEdge )
                        | _ -> false
                    if keepGoing then           
                        for recData in QueryNodes(nextLevel, fixedStep).Result do
                            yield recData
                    else if fixedStep.Next <> null then
                        for recData in QueryNodes(nextLevel, fixedStep.Next).Result do
                            yield recData
                else if fixedStep.Next <> null then
                    for recData in QueryNodes(nextLevel, fixedStep.Next).Result do
                        yield recData
                ()    
            })
    
                    
    interface IStorage with
        member x.Nodes = 
            // return local nodes before remote nodes
            // let just start by pulling nodes from the index.
            seq {
                let req =
                    seq {
                            for bc,t,part in PartitionWriters do
                                yield part.Index().Iter()
                                    |> Seq.map(fun ptrs ->
                                            let tcs = new TaskCompletionSource<Node[]>()
                                            let written = bc.Writer.WriteAsync(Read(tcs, ptrs.Pointers_.ToArray()))
                                            written, tcs.Task)
                        } |> Array.ofSeq
                            

                for (written, result) in req |> Seq.collect(fun x -> x) do
                    if written.IsCompletedSuccessfully then
                        result.Wait()
                        yield result.Result |> mergeNodesById
                                  
                                  
                    else 
                        written.AsTask().Wait()
                        result.Wait()
                        yield result.Result |> mergeNodesById
                
            }
            |> Seq.collect(fun x -> x)
            
            // todo return remote nodes
            
        member x.Flush () = Flush()
            
        member this.Add (nodes:seq<Node>) = 
            Task.Factory.StartNew(fun () -> 
                use addTimer = config.Metrics.Measure.Timer.Time(Metrics.FileStoreMetrics.AddTimer)
                // TODO: Might need to have multiple add functions so the caller can specify a time for the operation
                // Add time here so it's the same for all TMDs
                let nowInt = DateTime.UtcNow.ToBinary()
                
                let partitionLists = 
                    seq {for i in 0 .. (config.ParitionCount - 1) do 
                         yield new System.Collections.Generic.List<Node>()}
                    |> Array.ofSeq
                
                
                let lstNodes = nodes |> List.ofSeq
                let count = int64 lstNodes.Length  

                Parallel.For(0,lstNodes.Length,(fun i ->
                    let node = lstNodes.[i]
                    setTimestamps node nowInt
                    let nodeHash = Utils.GetAddressBlockHash node.Id
                    let partition = Utils.GetPartitionFromHash config.ParitionCount nodeHash
                    let plist = partitionLists.[partition] 
                    lock (plist) (fun () -> plist.Add node) 
                )) |> ignore
               
                partitionLists
                    |> Seq.iteri (fun i list ->
                        if (list.Count > 0) then
                            let tcs = new TaskCompletionSource<unit>(TaskCreationOptions.AttachedToParent)         
                            let (bc,_,_) = PartitionWriters.[i]
                            while bc.Writer.TryWrite ( Add(tcs,list)) = false do ()
                        )
                
                config.Metrics.Measure.Meter.Mark(Metrics.FileStoreMetrics.AddFragmentMeter, count)
                
                )
                
                        
        member x.Remove (nodes:seq<NodeID>) = raise (new NotImplementedException())
        member x.Items (addressBlock:seq<NodeID>, follow: Step) = QueryNodes(addressBlock, follow)
        member x.First (predicate: (Node -> bool)) = raise (new NotImplementedException())
        member x.Stop () =  
            Flush()
            for (bc,t,part) in PartitionWriters do
                bc.Writer.Complete()
                t.Join() // wait for shard threads to stop    
