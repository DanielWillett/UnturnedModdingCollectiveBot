using Discord;
using Discord.Interactions;

namespace UnturnedModdingCollective.Models.Modals;

#nullable disable
public class ReviewRequestModal : IModal
{
    public string Title => "Submit Portfolio";

    [RequiredInput]
    [InputLabel("Primary Steam Account ID")]
    [ModalTextInput("primary_steam_acct", placeholder: "76500000000000000 - https://steamid.io/", minLength: 17, maxLength: 17)]
    public ulong PrimarySteamAccount64 { get; set; }

    [RequiredInput]
    [InputLabel("What areas of modding are you experienced in?")]
    [ModalTextInput("modding_experience", style: TextInputStyle.Paragraph, placeholder: "Plugins, Unity, modeling, mapping, etc.", maxLength: 256)]
    public string ModdingExperience { get; set; }

    [RequiredInput]
    [InputLabel("Where'd you hear about us?")]
    [ModalTextInput("invite_src", style: TextInputStyle.Paragraph, maxLength: 256)]
    public string InviteSource { get; set; }
}
