using RethinkDb.Driver.Extras.Dao;

namespace RH
{
    public class RethinkObject<T, Guid> where T : IDocument<Guid>, new()
    {
        public Guid Id { get; set; }
    }
}