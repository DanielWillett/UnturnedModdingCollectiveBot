using Discord;

namespace UnturnedModdingCollective.Services;
public class PollFactory
{
    public const string PollYesText = "Yes";
    public const string PollNoText = "No";
    public virtual PollProperties CreateYesNoPoll(string question, TimeSpan duration)
    {
        if (duration > TimeSpan.FromDays(7d))
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be less than or equal to 7 days.");

        if (question.Length > 300)
            throw new ArgumentOutOfRangeException(nameof(question), "Question must not be longer than 300 characters.");

        PollProperties properties = new PollProperties
        {
            Duration = (uint)Math.Round(duration.TotalHours),
            LayoutType = PollLayout.Default,
            Question = new PollMediaProperties
            {
                Text = question
            },
            Answers =
            [
                new PollMediaProperties
                {
                    Emoji = new Emoji("\U00002705"),
                    Text = PollYesText
                },
                new PollMediaProperties
                {
                    Emoji = new Emoji("\U0000274C"),
                    Text = PollNoText
                }
            ]
        };

        return properties;
    }
}
