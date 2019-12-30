using System;

namespace RH
{
    internal class RethinkItem
    {
        public string Table { get; set; }
        public Guid Id { get; set; }

        public object LoadedObject { get; set; }
    }
}