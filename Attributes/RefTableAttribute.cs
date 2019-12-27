using System;

namespace RH.Attributes
{
    public class RefTableAttribute : Attribute
    {
        public string Table { get; }

        public RefTableAttribute(string table)
        {
            Table = table;
        }
    }
}