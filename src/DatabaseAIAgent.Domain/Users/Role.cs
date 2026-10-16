using Microsoft.AspNetCore.Identity;

namespace DatabaseAIAgent.Domain.Users;

public sealed class Role : IdentityRole<Guid>
{
    public Role()
    {
        Id = Guid.CreateVersion7();
    }

    public Role(string name)
        : this()
    {
        Name = name;
        NormalizedName = name.ToUpperInvariant();
    }
}
