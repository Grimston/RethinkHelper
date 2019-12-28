using System;

namespace RH
{
    internal class SharedListItem
    {
        public string Table { get; set; }
        public string ParentName { get; set; }
        public string ChildName { get; set; }

        public Guid ChildGuid { get; set; }
        public Guid SharedId { get; set; }
    }
}