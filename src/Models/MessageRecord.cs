using System.ComponentModel.DataAnnotations;

namespace App.Models;

public class MessageRecord
{
    [Key]
    public int Id { get; set; }

    public ulong MessageId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong GuildId { get; set; }
    public ulong AuthorId { get; set; }

    public string AuthorName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? Attachments { get; set; }
}
