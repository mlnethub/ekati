using System;
using System.Buffers.Text;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using Ahghee;
using Ahghee.Grpc;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using cli_grammer;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Array = Ahghee.Grpc.Array;
using Range = Ahghee.Grpc.Range;
using Utils = Ahghee.Grpc.Utils;

namespace cli
{
    public static class ContextExtensions
    {
        public static NodeID ToNodeID(this AHGHEEParser.NodeidContext id)
        {
            var json = id.obj();
                                
            if (json != null)
            {
                var text = json.GetText();
                var ab = Google.Protobuf.JsonParser.Default.Parse<NodeID>(text);
                ab.Pointer = Utils.NullMemoryPointer();
                return ab;
            }
        
            var dburi = id.id();

            var ac = new NodeID
            {
                Iri = dburi.GetText().Trim('"'),
                Pointer = Utils.NullMemoryPointer()
            };
            if (id.remote() != null)
            {
                ac.Remote = id.remote().GetText().Trim('"');
            }

            return ac;
        }

        public static TMD ToDataBlock(this NTRIPLESParser.ObjContext ctx, Func<string, string> BNToId)
        {
            // VALUE with optional stuffs
            var db = new DataBlock();

            var literalString = ctx.literal()?.STRING()?.GetText().Trim('"');
            if (literalString != null)
            {
                db.Str = literalString;
                var tmd = new TMD();
                tmd.Data = db;
                
                var literalStringTypeIRI = ctx.literal()?.IRI()?.GetText().Trim('<', '>');
                if (literalStringTypeIRI != null)
                {
                    tmd.MetaData = new DataBlock
                    {
                        Nodeid = literalStringTypeIRI.ToNodeID()
                    };
                }
                var literalStringLang = ctx.literal()?.LANGTAG()?.GetText();
                if (literalStringLang != null)
                {
                    tmd.MetaData = new DataBlock
                    {
                        Str = $"lang:{literalStringLang}"
                    };
                }

                return tmd;
            }
            
            // Value Pointer
            var objPointer = ctx.IRI()?.GetText()?.Trim('<', '>');
            if (objPointer != null)
            {
                db.Nodeid = objPointer.ToNodeID();
                return new TMD
                {
                    Data = db
                };
            }
            
            // Value BlankNode, got this far, we have to have a blankNode, or something is broke.
            var objVal = ctx.BLANKNODE().GetText();

            var objBn = BNToId(objVal);
            db.Nodeid = objBn.ToNodeID();
            return new TMD
            {
                Data = db
            };
        }

        public static DataBlock ToDataBlock(this NTRIPLESParser.PredContext ctx)
        {
            var pred = ctx.GetText().Trim('<', '>');
            return pred.ToDataBlockNodeID();
        }

        public static NodeID ToNodeId(this NTRIPLESParser.SubjContext ctx, Func<string,string> BNToId)
        {
            var subIRI = ctx.IRI()?.GetText()?.Trim('<', '>');
            if (subIRI == null)
            {
                var subjBN = ctx.BLANKNODE().GetText();
                subIRI = BNToId(subjBN);
            }
            return new NodeID {Iri = subIRI};
        }

        public static NodeID ToNodeID(this string str)
        {
            return new NodeID{Iri = str};
        }
        public static DataBlock ToDataBlockNodeID(this string str)
        {
            var nid = new NodeID {Iri = str};
            nid.Pointer = new MemoryPointer();
            return new DataBlock
            {
                Nodeid = nid
            };
        }

        public static Array ToArray(this AHGHEEParser.ArrContext arr)
        {
            var outt = new Array();
            foreach (var ar in arr.value())
            {
                outt.Item.Add(ar.ToDataBlock());
            }

            return outt;
        }
        public static Map ToMap(this AHGHEEParser.KvpsContext kvps)
        {
            try
            {
                var m = new Map();
                foreach (var pair in kvps.pair())
                {
                    var kv = new KeyValue();
                    kv.Key = new TMD();
                    kv.Key.Data = new DataBlock();
                    kv.Value = new TMD();
                    if (pair.kvp() != null)
                    {
                        kv.Key.Data.Str = pair.kvp().STRING().GetText().Trim('"');    
                        kv.Value.Data = pair.kvp().value().ToDataBlock();
                    } else if(pair.edge()!=null)
                    {
                        kv.Key.Data.Str = pair.edge().STRING(0).GetText().Trim('"');
                        kv.Value.Data = pair.edge().STRING(1).GetText().Trim('"').ToDataBlockNodeID();
                    }else if(pair.fedge()!=null)
                    {
                        kv.Key.Data = pair.edge().STRING(0).GetText().Trim('"').ToDataBlockNodeID();
                        kv.Value.Data.Str = pair.edge().STRING(1).GetText().Trim('"');
                    }else if(pair.dedge()!=null)
                    {
                        kv.Key.Data = pair.edge().STRING(0).GetText().Trim('"').ToDataBlockNodeID();
                        kv.Value.Data = pair.edge().STRING(1).GetText().Trim('"').ToDataBlockNodeID();
                    }
                                
                    m.Attributes.Add(kv);
                }
                return m;
            }
            catch (Exception ex)
            {
                Console.WriteLine(kvps.GetText());
                Console.WriteLine(ex.Message + "\n" + ex.StackTrace);
                throw;
            }

            
        }
        public static DataBlock ToDataBlock(this AHGHEEParser.ValueContext v)
        {
            var db = new DataBlock();

            if (v.STRING() != null)
            {
                db.Str = v.STRING().GetText().Trim('"');
            } else if (v.NUMBER() != null)
            {
                var numberstr = v.NUMBER().GetText();
                if (Int32.TryParse(numberstr, out var i32))
                {
                    db.I32 = i32;
                } else if (Int64.TryParse(numberstr, out var i64))
                {
                    db.I64 = i64;
                } else if (UInt64.TryParse(numberstr, out var ui64))
                {
                    db.Ui64 = ui64;
                } else if (Single.TryParse(numberstr, out var sing))
                {
                    db.F = sing;
                }
                else if(Double.TryParse(numberstr, out var doub))
                {
                    db.D = doub;
                }
            } else if (v.obj() != null && v.obj().kvps() != null)
            {
                db.Map = v.obj().kvps().ToMap();
                // try
                // {
                //     var nid = JsonParser.Default.Parse<NodeID>(obj);
                //     if (nid != null)
                //     {
                //         db.Nodeid = nid;
                //         db.Nodeid.Pointer = new MemoryPointer();
                //     }
                // }
                // catch (Exception e)
                // {
                // }
                // try
                // {
                //     var mp = JsonParser.Default.Parse<MemoryPointer>(obj);
                //     if (mp != null)
                //     {
                //         db.Memorypointer = mp;
                //     }
                // }
                // catch (Exception e)
                // {
                // }
                // try
                // {
                //     var tb = JsonParser.Default.Parse<TypeBytes>(obj);
                //     if (tb != null)
                //     {
                //         db.Metabytes = tb;
                //     }
                // }
                // catch (Exception e)
                // {
                // }
                // json object is not a native value type

            } else if (v.arr() != null)
            {
                db.Array = v.arr().ToArray();
                // array is not a native value type.

            } else if (!string.IsNullOrWhiteSpace(v.GetText()))
            {
                var t = v.GetText().Trim('"');
                if (Boolean.TryParse(t, out var boo))
                {
                    db.B = boo;
                } else if (t == "null")
                {
                    // do nothing.    
                }
            }
            // json fallback
            if (db.DataCase == DataBlock.DataOneofCase.None)
            {
                var jsonstuff = v.GetText(); // must be some kind of json at this point.. (I hope :P)
                                
                var tb = new TypeBytes();
                tb.Typeiri = "application/json";
                tb.Bytes = ByteString.CopyFromUtf8(jsonstuff);
                db.Metabytes = tb;
            }

            return db;
        }

        public static FollowOperator.Types.FollowAny ToFollowAny(this AHGHEEParser.AnynumContext ctx)
        {
            return new FollowOperator.Types.FollowAny()
            {
                Range = ctx.range()?.ToRange()
            };
        }

        public static Range ToRange(this AHGHEEParser.RangeContext ctx)
        {
            return new Range()
            {
                From = Int32.Parse(ctx.@from()?.GetText() ?? "0"),
                To = Int32.Parse(ctx.to().GetText() ?? "0")
            };
        }

        public static FollowOperator.Types.EdgeNum ToEdgeNum(this AHGHEEParser.EdgenumContext ctx)
        {
            var e = new FollowOperator.Types.EdgeNum();
            var db = ctx.value()?.ToDataBlock();
            if (db != null)
            {
                e.EdgeRange = new FollowOperator.Types.EdgeRange
                {
                    Edge = db,
                    Range = ctx.range().ToRange()
                };
            }
            else
            {
                e.EdgeCmp = new FollowOperator.Types.EdgeCMP()
                {
                    Left = ctx.edgenum(0).ToEdgeNum(),
                    BOOLOP =ctx.BOOLOP().GetText(),
                    Right = ctx.edgenum(1).ToEdgeNum()
                };
            }

            return e;
        }
        public static FollowOperator ToFollowOperator(this AHGHEEParser.FollowContext ctx)
        {
            var fo = new FollowOperator();

            if (ctx.anynum() != null)
            {
                fo.FollowAny = ctx.anynum().ToFollowAny();
            }
            else
            {
                fo.FollowEdge = ctx.edgenum().ToEdgeNum();
            }
            return fo;
        }

        public static FilterOperator.Types.Compare ToCompare(this AHGHEEParser.CompareContext ctx)
        {
            var key = ctx.wfkey()?.value().ToDataBlock();
            if (key!=null)
            {
                var value = ctx.wfvalue()?.value().ToDataBlock();
                return new FilterOperator.Types.Compare()
                {
                    KevValueCmp = new FilterOperator.Types.CompareKeyValue()
                    {
                        Property = key,
                        MATHOP = ctx.MATHOP().GetText(),
                        Value = value
                    }
                };
            }

            var left = ctx.compare(0);
            var right = ctx.compare(1);
            
            return new FilterOperator.Types.Compare()
            {
                CompoundCmp = new FilterOperator.Types.CompareCompound()
                {
                    Left = left.ToCompare(),
                    BOOLOP = ctx.BOOLOP().GetText(),
                    Right = right.ToCompare()
                }
            };
        }
        
        public static FilterOperator ToFilterOperator(this AHGHEEParser.WherefilterContext ctx)
        {
            var filter = new FilterOperator();
            filter.Compare = ctx.compare()?.ToCompare();
            return filter;
        }

        public static SkipFilter ToSkipOperator(this AHGHEEParser.SkipfilterContext ctx)
        {
            var filter = new SkipFilter();
            filter.Value = Int32.Parse(ctx.NUMBER().GetText());
            return filter;
        }
        
        public static LimitFilter ToLimitOperator(this AHGHEEParser.LimitfilterContext ctx)
        {
            var filter = new LimitFilter();
            filter.Value = Int32.Parse(ctx.NUMBER().GetText());
            return filter;
        }

        public static FieldsOperator.Types.Clude ToClude(this AHGHEEParser.CludeContext ctx)
        {
            FieldsOperator.Types.CludeOp.Types.CludePart ToCludePart(AHGHEEParser.CludepartContext cludeOpChild)
            {
                var cp = new FieldsOperator.Types.CludeOp.Types.CludePart();
                if (cludeOpChild.CARET() != null && cludeOpChild.STRING() != null)
                {
                    cp.CarrotStringMatch = cludeOpChild.STRING().GetText().Trim('"');
                }else if (cludeOpChild.STRING() != null)
                {
                    cp.StringMatch = cludeOpChild.STRING().GetText().Trim('"');
                }else if (cludeOpChild.CARET() != null)
                {
                    cp.IsCaret = true;
                }else if (cludeOpChild.STAR() != null)
                {
                    cp.IsStar = true;
                }else if (cludeOpChild.TYPEINT() != null)
                {
                    cp.IsTypeInt = true;
                }else if (cludeOpChild.TYPEFLOAT() != null)
                {
                    cp.IsTypeFloat = true;
                }else if (cludeOpChild.TYPESTRING() != null)
                {
                    cp.IsTypeString = true;
                }
                
                return cp;
            }
            
            var clude = new FieldsOperator.Types.Clude();
            if (ctx.cludeop() != null)
            {
                var cludeop = ctx.cludeop();
                clude.Op = new FieldsOperator.Types.CludeOp();
                if (cludeop.ChildCount > 1)
                {
                    clude.Op.Left = ToCludePart(cludeop.cludepart(0));
                    clude.Op.Right = ToCludePart(cludeop.cludepart(1));
                }
            } else if (ctx.include() != null)
            {
                if (ctx.clude() != null && ctx.clude().Length == 1)
                {
                    clude.Twoclude = new FieldsOperator.Types.TwoClude();
                    clude.Twoclude.Include = ToClude(ctx.include().clude());
                    clude.Twoclude.Left = ToClude(ctx.clude(0));
                }
                else
                {
                    clude.Include = ToClude(ctx.include().clude());
                }
            } else if (ctx.exclude() != null)
            {
                if (ctx.clude() != null && ctx.clude().Length == 1)
                {
                    clude.Twoclude = new FieldsOperator.Types.TwoClude();
                    clude.Twoclude.Exclude = ToClude(ctx.exclude().clude());
                    clude.Twoclude.Left = ToClude(ctx.clude(0));
                }
                else
                {
                    clude.Include = ToClude(ctx.include().clude());
                }
            } else if (ctx.clude() != null && ctx.clude().Length > 0)
            {
                clude.List = new FieldsOperator.Types.CludeList();
                foreach (var c in ctx.clude())
                {
                   clude.List.Cludes.Add(ToClude(c)); 
                }
            }
            return clude;
        }
        public static FieldsOperator ToFieldsOperator(this AHGHEEParser.FieldsContext ctx)
        {
            var fields = new FieldsOperator();
            fields.Clude = ctx.clude().ToClude();
            return fields;
        }
        
        public static Step ToPipeFlow(this AHGHEEParser.PipeContext ctx)
        {
            var cmd = ctx.pipecmd();
            if (cmd.follow() != null)
            {
                var follow = cmd.follow().ToFollowOperator();
                return new Step()
                {
                    Follow = follow,
                    Next = ctx.pipe()?.ToPipeFlow()
                };
            } 
            
            if (cmd.wherefilter() != null)
            {
                var filter = cmd.wherefilter().ToFilterOperator();
                return new Step()
                {
                    Filter = filter,
                    Next = ctx.pipe()?.ToPipeFlow()
                };
            }

            if (cmd.skipfilter() != null)
            {
                var skip = cmd.skipfilter().ToSkipOperator();
                return new Step()
                {
                    Skip = skip,
                    Next = ctx.pipe()?.ToPipeFlow()
                };
            }
            
            if (cmd.limitfilter() != null)
            {
                var limit = cmd.limitfilter().ToLimitOperator();
                return new Step()
                {
                    Limit = limit,
                    Next = ctx.pipe()?.ToPipeFlow()
                };
            }

            if (cmd.fields() != null)
            {
                var fields = cmd.fields().ToFieldsOperator();
                return new Step()
                {
                    Fields = fields,
                    Next = ctx.pipe()?.ToPipeFlow()
                };
            }
            
            throw new NotImplementedException("Only Follow and Filter are available in PipeContext so far.");
        }
    }
}