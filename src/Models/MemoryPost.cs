using System.ComponentModel.DataAnnotations;

namespace App.Models;

public class MemoryPost
{
    [Key]
    public int Id { get; set; }

    public ulong GuildId { get; set; }
    public string PostType { get; set; } = string.Empty;
    public DateTime LastPostedAt { get; set; }
}
