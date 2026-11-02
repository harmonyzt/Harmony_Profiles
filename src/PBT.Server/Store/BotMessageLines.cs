namespace PBT.Server;

// Keyed "event:tone". Timmy lines are lowercased when sent.
internal static class BotMessageLines
{
    internal static readonly Dictionary<string, string[]> Pools = new()
    {
        ["wipe:timmy"] = ["how did you kill all of us", "bro we were a full squad... how", "ok you are actually cracked. we had 4 people", "my friends are so mad at me rn lol"],
        ["wipe:chill"] = ["Clean squad wipe on {map}. Respect.", "All of us and you still walked out. gg", "Nice work on {map}, that was a proper wipe.", "Didn't think a solo could take our whole team. Well played."],
        ["wipe:salty"] = ["a whole squad and you got lucky every time. sure", "enjoy the kits, cheater", "no way thats legit. reported", "you're not that good, my team is just bad"],
        ["wipe:streamer"] = ["chat is going insane rn, gg on the squad wipe", "clipping that one. you wiped my whole squad on stream", "my viewers think you're hacking lol. gg", "that wipe is going in the highlights. respect"],

        ["longshot:timmy"] = ["where did that even come from", "{distance} meters?? i didnt even hear it", "i was just looting man", "how do u see that far"],
        ["longshot:chill"] = ["{distance}m shot. Nice one.", "Didn't see you at all. Good shot from {distance}m.", "Solid long range on {map}, gg.", "That was a clean {distance}m shot."],
        ["longshot:salty"] = ["{distance}m with that? lucky", "camping a bush for {distance}m shots, real skill", "come fight me up close next time", "enjoy your bush"],
        ["longshot:streamer"] = ["chat wants your scope setup. {distance}m is crazy", "{distance}m on stream, gg", "my mods are checking your profile. that shot was nuts", "ok that {distance}m shot gets a clip"],

        ["extract:timmy"] = ["i was literally at extract", "bro i was 5 seconds from leaving", "noooo i had everything in my bag", "why at extract man"],
        ["extract:chill"] = ["Extract camping on {map}. Fair play.", "Got me right at extract. gg.", "Had a good raid till you. Nice catch at the exit.", "Knew someone would be watching that exit."],
        ["extract:salty"] = ["extract camper. classic", "imagine waiting at extract all raid", "go loot something yourself", "rat camping extract, nice"],
        ["extract:streamer"] = ["chat called the extract camp and i still walked into it", "an extract camp on stream, painful. gg", "chat is laughing at me rn. well played at extract", "5 seconds from extract on stream. you got me"],

        ["loaded:timmy"] = ["that was my whole stash", "i spent all my money on that kit", "please give my stuff back lol", "that was my first good raid"],
        ["loaded:chill"] = ["Enjoy the loot, that was a great raid for me until you.", "You got a good bag there. gg.", "Best raid of my week and you took it. Well played.", "Take care of that loot."],
        ["loaded:salty"] = ["enjoy my loot you didnt earn", "hope you get killed on the way out", "that bag is cursed now", "great, you took my best raid. thanks"],
        ["loaded:streamer"] = ["chat is crying over that bag. gg", "that was my best raid on stream. you owe me", "my viewers watched me lose all that. gg", "enjoy the loot, chat says hi"],

        ["boss:timmy"] = ["you killed the boss AND me??", "how are you so good", "i was hiding from the boss and then you came", "bro you have the boss loot already"],
        ["boss:chill"] = ["Boss and me in one raid on {map}. Good day for you.", "Saw you take the boss. Should have stayed away. gg", "Nice boss kill. I was next I guess.", "Busy raid on {map}. gg."],
        ["boss:salty"] = ["boss hunting and pmc hunting, get a life", "you needed the boss kill AND my kit?", "greedy raid", "leave something for the rest of us"],
        ["boss:streamer"] = ["we were going for the boss on stream, you beat us to both. gg", "chat says you ran {map}", "boss and me in one raid, clip it", "that boss kill was ours. gg though"],

        ["rampage:timmy"] = ["you killed so many people before i got you", "i got the guy who killed everyone!!", "omg i got you. you had {kills} kills", "my friends wont believe this"],
        ["rampage:chill"] = ["You had a good run with {kills} kills. gg.", "Saw your trail of bodies on {map}. Had to end it. gg", "{kills} kills is a solid raid. Unlucky.", "Good raid until me. Well played."],
        ["rampage:salty"] = ["{kills} kills and you still died to me lol", "rampage over", "ez. not so tough now", "should have extracted"],
        ["rampage:streamer"] = ["chat was counting your {kills} kills. ended it on stream", "ended a {kills} kill raid on stream. gg", "my viewers loved that one. gg", "sorry for ending the rampage. clip of the day"],

        ["underdog:timmy"] = ["i killed a level {playerLevel}!!!", "omg you're level {playerLevel} and i got you", "screenshotting this", "i only have a pistol lol"],
        ["underdog:chill"] = ["Didn't expect to beat a level {playerLevel}. gg", "Got lucky on that one. Good game.", "Respect, you'll get me next time.", "Fair fight. gg"],
        ["underdog:salty"] = ["level {playerLevel} and you died to me. lol", "all that level and still bad", "ez", "uninstall"],
        ["underdog:streamer"] = ["chat, a level {playerLevel} just died to my starter kit", "underdog win on stream. gg", "ok that one goes in the highlights", "gg, you made my stream today"],

        ["kit:timmy"] = ["thank you for the gear!!!", "i'm rich now", "is this armor good? lol", "i cant even carry all of this"],
        ["kit:chill"] = ["Thanks for the kit. gg.", "That was some expensive gear. gg", "Your kit will be in good hands.", "Nice loadout. Unlucky."],
        ["kit:salty"] = ["thanks for the free kit", "bring more gear next time", "all that gear and still dead", "ez loot"],
        ["kit:streamer"] = ["chat say thanks for the kit", "kit donation on stream, appreciated", "chat is rating your loadout 10/10. gg", "thanks for the gear, it's going in the stash"]
    };
}
