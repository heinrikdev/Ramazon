using Verse;

namespace Ramazon
{
    public class ModEntry : Mod
    {
        public static RamazonSettings Settings;

        public ModEntry(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RamazonSettings>();
        }

        public override string SettingsCategory() => "Ramazon";

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Settings.DoSettingsWindowContents(inRect);
        }
    }
}