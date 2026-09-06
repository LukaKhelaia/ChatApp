using System.Text.RegularExpressions;
using System.Text.Json.Serialization;


namespace ChatApp.Models
{
    public class UserGroup
    {
        public int Id { get; set; }
        public required string UserName { get; set; }

        public int GroupId { get; set; }

        /// <summary>
        /// Can add members and remove ordinary ones. The group's creator is an
        /// admin whether or not this flag is set - Group.CreatorUserName is the
        /// authority there - so this column only ever describes the admins the
        /// creator has appointed.
        /// </summary>
        public bool IsAdmin { get; set; }

        [JsonIgnore]
        public Group Group { get; set; } = null!;
    }
}
