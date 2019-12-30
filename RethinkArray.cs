using System;
using System.Collections;
using System.Collections.Generic;

namespace RH
{
    public class RethinkArray<T> : IList<T>
    {
        internal List<RethinkItem> Items { get; set; } = new List<RethinkItem>(0);

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                if (item.LoadedObject == null)
                {
                    item.LoadedObject = RethinkHelper.GetObject<T>(item.Id, item.Table);

                    Items[i] = item; //Save back into the list
                }

                yield return (T) item.LoadedObject;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Add(T item)
        {
            Items.Add(new RethinkItem()
            {
                LoadedObject = item
            });
        }

        internal void AddRethinkItem(RethinkItem item)
        {
            Items.Add(item);
        }

        public void Clear()
        {
            Items.Clear();
        }

        public bool Contains(T item)
        {
            return Items.FindIndex(x => x.LoadedObject == (object) item) >= 0;
        }

        /// <summary>
        /// This is not implemented at this time
        /// </summary>
        /// <param name="array"></param>
        /// <param name="arrayIndex"></param>
        /// <exception cref="NotImplementedException"></exception>
        public void CopyTo(T[] array, int arrayIndex)
        {
            throw new System.NotImplementedException();
        }

        public bool Remove(T item)
        {
            var index = Items.FindIndex(x => x.LoadedObject == (object) item);

            if (index < 0) return false;

            Items.RemoveAt(index);
            return true;
        }

        public int Count => Items.Count;

        public bool IsReadOnly => false;

        public int IndexOf(T item)
        {
            return Items.FindIndex(x => x.LoadedObject == (object) item);
        }

        public void Insert(int index, T item)
        {
            Items.Insert(index, new RethinkItem()
            {
                LoadedObject = item
            });
        }

        public void RemoveAt(int index)
        {
            Items.RemoveAt(index);
        }

        public T this[int i]
        {
            get
            {
                var item = Items[i];
                if (item.LoadedObject == null)
                {
                    item.LoadedObject = RethinkHelper.GetObject<T>(item.Id, item.Table);

                    Items[i] = item; //Save back into the list
                }

                return (T) item.LoadedObject;
            }
            set =>
                Items[i] = new RethinkItem
                {
                    LoadedObject = value
                };
        }
    }
}