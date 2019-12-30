RethinkDB Helper heavily inspired by RedBeanPHP more features to come in the near future.

## Example data class
```CSharp
public class User : RethinkObject<User>, IDocument<Guid>
{
    [SecondaryIndex] public string EmailAddress { get; set; }
    public string Password { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }

    [RefTable(nameof(data.Organization))]
    [NoTrash]
    public Organization Organization { get; set; }

    //Deffered loading of data, only the shared table is loaded into memory first.
    [RefTable(nameof(Role))] [SharedTable] public RethinkArray<Role> Roles { get; set; }
}
```

## Usage example
```CSharp
RethinkHelper.Connect("127.0.0.1", "test");

var user = RethinkHelper.Dispense<User>();
user.Username = "User_Username";
user.Password = "User_Password";
user.UserAttributes = new RethinkArray<UserAttributes>
{
    new UserAttributes
    {
        Name = "Test1",
        Value = "Test1 Value"
    },
    new UserAttributes
    {
        Name = "Test2",
        Value = "Test2 Value"
    },
};
user.Roles = new RethinkArray<Role>
{
    new Role
    {
        Name = "Testing Role",
        Permissions = new RethinkArray<Permission>
        {
            new Permission
            {
                Name = "Permission 1",
                Type = true
            },
            new Permission
            {
                Name = "Permission 2",
                Type = false
            }
        }
    }
};

//Ensures the table exists with correct indexes, creates all child tables if needed.
var newId = RethinkHelper.Store(user);

user = RethinkHelper.FindOne<User>(newId); //Gets the new user from Rethink and rebuilds the data structure

foreach (var role in user.Roles)
{
    //The role is loaded from the database during enumeration, this slows down the loop
    //But does mean the inital response is faster and we only load what is needed.
    foreach (var permission in role.Permissions)
    {
        //Same deal as long as a property is using RethinkArray<>
    }
}

/**
 * Delete the record and all direct child records, anything marked with NoTrash is kept.
 * Any SharedTable's children are kept, only the link between the two is removed.
 * This allows for things like Roles and Permissions to continue existing.
**/
RethinkHelper.Trash(user);
//Note the RethinkObject itself will remain unmodified and can easily be restored just by storing it again.
```
