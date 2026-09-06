using System;

namespace ChatApp.Models
{
    public class GroupChatDeletion
    {
        public int Id { get; set; }

        public string UserName { get; set; } = null!;

        public int GroupId { get; set; }

        public DateTime? DeletedAt { get; set; }

        public bool IsHidden { get; set; } = true;
    }
}
