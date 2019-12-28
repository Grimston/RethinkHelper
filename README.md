RethinkDB Helper heavily inspired by RedBeanPHP more features to come in the near future.

##Example data class
```CSharp
public class User : RethinkObject<User, Guid>, IDocument<Guid>
{
    [SecondaryIndex] public string EmailAddress { get; set; }
    public string Password { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }

    [RefTable(nameof(data.Organization))]
    [NoTrash]
    public Organization Organization { get; set; }

    //NoTrash is implied for SharedTables
    [RefTable(nameof(Role))] [SharedTable] public Role[] Roles { get; set; }
}
```

##Usage example
```CSharp
RethinkHelper.Connect("127.0.0.1", "test");

var user = RethinkHelper.Dispense<User>();
user.FirstName = "Joe";
user.LastName = "Blow";
user.EmailAddress = "email@example.com";
user.Password = "MySecurePassword";
user.Organization = new Organization
{
    Address = "123 Mythical Lane",
    Enabled = true,
    Name = "Testing Company"
};
user.Roles = new[]
{
    new Role
    {
        Name = "Administrator",
        Permission = new[]
        {
            new Permission
            {
                Name = "Administrator",
                Type = true
            },
            new Permission
            {
                Name = "Database View",
                Type = true
            }
        }
    }
};

//Ensures the table exists with correct indexes, creates all child tables if needed.
var newId = RethinkHelper.Store(user);

user = RethinkHelper.FindOne<User>(newId); //Gets the new user from Rethink and rebuilds the data structure

/**
 * Delete the record and all direct child records, anything marked with NoTrash is kept.
 * Any SharedTable's children are kept, only the link between the two is removed.
 * This allows for things like Roles and Permissions to continue existing.
**/
RethinkHelper.Trash(user);
//Note the RethinkObject itself will remain unmodified and can easily be restored just by storing it again.
```
