﻿
using System;
using System.Collections;
using System.Data.HashFunction.MurmurHash;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using pb = global::Google.Protobuf;
using pbc = global::Google.Protobuf.Collections;
using pbr = global::Google.Protobuf.Reflection;
using scg = global::System.Collections.Generic;

namespace Ahghee.Grpc
{
    public sealed partial class NodeID : IComparable, IComparable<NodeID>, scg.IEqualityComparer<NodeID>
    {
        private static IMurmurHash3 hasher = System.Data.HashFunction.MurmurHash.MurmurHash3Factory.Instance.Create();
        public int CompareTo(object obj)
        {
            switch (obj)
            {
                case null:
                    return 1;
                case NodeID other:
                    return CompareTo(other);
            }

            throw new ArgumentException("Object is not a NodeID");
        }

        public int CompareTo(NodeID other)
        {
            if (other == null)
                return 1;
            var gcompare = string.Compare(Graph, other.Graph, StringComparison.Ordinal);
            if (gcompare != 0)
                return gcompare;
            var nidcompare = string.Compare(Nodeid, other.Nodeid, StringComparison.Ordinal);
            return nidcompare;
        }
        
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
        public bool Equals(NodeID other) {
            if (ReferenceEquals(other, null)) {
                return false;
            }
            if (ReferenceEquals(other, this)) {
                return true;
            }
            if (Graph != other.Graph) return false;
            if (Nodeid != other.Nodeid) return false;
            return true;
        }

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
        public override int GetHashCode() {
            return GetHashCode(this);
        }

        public bool Equals(NodeID x, NodeID y)
        {
            return x != null && x.Equals(y);
        }

        public int GetHashCode(NodeID obj)
        {
            var sourceG = System.Text.Encoding.UTF8.GetBytes(obj.Graph);
            var sourceN = System.Text.Encoding.UTF8.GetBytes(obj.Nodeid);
            var array = new byte[sourceG.Length + sourceN.Length];
            Array.Copy(sourceG, 0, array, 0, sourceG.Length);
            Array.Copy(sourceN, 0, array, sourceG.Length, sourceN.Length);
            var hash = hasher.ComputeHash(array);
            return BitConverter.ToInt32(hash.Hash, 0);
        }
    }
}