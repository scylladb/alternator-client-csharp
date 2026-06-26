// <copyright file="ReadOnlySet.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    internal sealed class ReadOnlySet<T> : ISet<T>, IReadOnlySet<T>
    {
        private readonly HashSet<T> items;

        public ReadOnlySet(IEnumerable<T> items, IEqualityComparer<T>? comparer = null)
        {
            this.items = new HashSet<T>(items, comparer);
        }

        public int Count => this.items.Count;

        public bool IsReadOnly => true;

        bool ISet<T>.Add(T item)
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public void Add(T item)
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public void IntersectWith(IEnumerable<T> other)
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            return this.items.IsProperSubsetOf(other);
        }

        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            return this.items.IsProperSupersetOf(other);
        }

        public bool IsSubsetOf(IEnumerable<T> other)
        {
            return this.items.IsSubsetOf(other);
        }

        public bool IsSupersetOf(IEnumerable<T> other)
        {
            return this.items.IsSupersetOf(other);
        }

        public bool Overlaps(IEnumerable<T> other)
        {
            return this.items.Overlaps(other);
        }

        public bool SetEquals(IEnumerable<T> other)
        {
            return this.items.SetEquals(other);
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public void UnionWith(IEnumerable<T> other)
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public void Clear()
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public bool Contains(T item)
        {
            return this.items.Contains(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            this.items.CopyTo(array, arrayIndex);
        }

        public bool Remove(T item)
        {
            throw new NotSupportedException("This set is read-only.");
        }

        public IEnumerator<T> GetEnumerator()
        {
            return this.items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}
