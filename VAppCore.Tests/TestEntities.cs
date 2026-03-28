namespace VAppCore.Tests;

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Age { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal Salary { get; set; }
    public string? Department { get; set; }
    public Address? Address { get; set; }
    public UserStatus Status { get; set; }
    public Guid? ExternalId { get; set; }
    public List<Order> Orders { get; set; } = [];
}

public class Address
{
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

public class Order
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
}

public enum UserStatus
{
    Active,
    Inactive,
    Suspended
}

public class UserQueryFilter : VQueryFilter<User>
{
    public UserQueryFilter()
    {
        Field(x => x.Id).Filterable().Sortable().Selectable();
        Field(x => x.Name).Filterable().Sortable().Selectable().WithAlias("username");
        Field(x => x.Email).Filterable().Selectable();
        Field(x => x.Age).Filterable().Sortable();
        Field(x => x.IsActive).Filterable();
        Field(x => x.CreatedAt).Filterable().Sortable();
        Field(x => x.Salary).Filterable();
        Field(x => x.Status).Filterable();
        Field(x => x.Department).Filterable().Selectable();
        Field(x => x.Address!.City).Filterable().Selectable();
        SetDefaultSort("-createdAt");
        SetDefaultSelect("id", "name", "email");
    }
}
