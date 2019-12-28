using System;
using RethinkDb.Driver.Extras.Dao;

namespace RH
{
    public class RethinkObject<T> where T : IDocument<Guid>, new()
    {
        public Guid Id { get; set; }

        /// <summary>
        /// Used internally to detect a shared record so we have an id of the shared item
        /// </summary>
        internal Guid SharedId { get; set; }
    }
}