RethinkDB Helper heavily inspired by RedBeanPHP more features to come in the near future.

Example data class
```CSharp
public class User : RethinkObject<User, Guid>, IDocument<Guid>
{
    [SecondaryIndex] public string EmailAddress { get; set; }
    public string Password { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }

    [RefTable(nameof(data.Organization))] public Organization Organization { get; set; }
    [RefTable(nameof(Role))] public Role[] Roles { get; set; }
}
```

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

//Delete the user, boolean is used to enable recursive mode which will delete all child records.
//TODO: Shared lists that are ignored, and need to be manually deleted. (Example: Permissions)
RethinkHelper.Trash(user, true);
```
