using System;
using System.Collections.Generic;

namespace ChatApp.Models
{
    public class Group
    {
        public int Id { get; set; }
        
        public string Name { get; set; } = string.Empty;

        public string CreatorUserName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string? ImageUrl { get; set; }

        public List<UserGroup> UserGroups { get; set; } = new();
    }
}
