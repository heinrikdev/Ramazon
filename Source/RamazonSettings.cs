using UnityEngine;
using Verse;

namespace Ramazon
{
    public class RamazonSettings : ModSettings
    {
        // BUY
        public float priceMultiplier = 1.30f;

        // SELL
        // você paga essa % como taxa e recebe (100 - taxa)%
        public float sellTaxPercent = 20f;

        // Stuffables (opcional)
        public bool showStuffables = false; // off por padrão
        public string defaultStuffDefName = "Steel";

        public override void ExposeData()
        {
            Scribe_Values.Look(ref priceMultiplier, "priceMultiplier", 1.30f);
            Scribe_Values.Look(ref sellTaxPercent, "sellTaxPercent", 20f);
            Scribe_Values.Look(ref showStuffables, "showStuffables", false);
            Scribe_Values.Look(ref defaultStuffDefName, "defaultStuffDefName", "Steel");
            base.ExposeData();
        }

        public void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            // --------- Preço de compra ----------
            // slider simples (a API 1.6 aceita a sobrecarga curta; arredondamos manualmente para 2 casas)
            list.Label($"Price multiplier: {priceMultiplier:0.00}×");
            var pmRect = list.GetRect(22f);
            priceMultiplier = Widgets.HorizontalSlider(pmRect, priceMultiplier, 1.00f, 3.00f, false);
            priceMultiplier = Mathf.Round(priceMultiplier * 100f) / 100f;

            list.Gap(6f);

            // --------- Taxa de venda ----------
            float receivePercent = 100f - sellTaxPercent;
            list.Label($"Selling pays {receivePercent:0}% of market value (fee {sellTaxPercent:0}%).");
            var taxRect = list.GetRect(22f);
            sellTaxPercent = Widgets.HorizontalSlider(taxRect, sellTaxPercent, 0f, 50f, false);
            sellTaxPercent = Mathf.Round(sellTaxPercent); // passos de 1%

            list.GapLine(6f);

            // --------- Stuffables (opcional) ----------
            list.CheckboxLabeled(
                "Allow stuffable items (experimental)",
                ref showStuffables,
                "If enabled, items made from stuff (apparel/weapons/buildables) will be spawned using a default stuff and cost will be roughly estimated."
            );

            if (showStuffables)
            {
                list.Label($"Default stuff defName: {defaultStuffDefName}");
                defaultStuffDefName = Widgets.TextField(list.GetRect(24f), defaultStuffDefName);
            }

            list.End();
        }
    }
}