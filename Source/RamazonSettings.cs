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
        public bool showStuffables = false;
        public string defaultStuffDefName = "Steel";

        // Animais
        public bool allowAnimals = false;

        // Venda: usar o valor REAL da instancia (material/qualidade/dano), como uma
        // caravana. Desligado, volta ao comportamento antigo: valor base do ThingDef.
        public bool useRealItemValue = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref priceMultiplier, "priceMultiplier", 1.30f);
            Scribe_Values.Look(ref sellTaxPercent, "sellTaxPercent", 20f);
            Scribe_Values.Look(ref showStuffables, "showStuffables", false);
            Scribe_Values.Look(ref defaultStuffDefName, "defaultStuffDefName", "Steel");
            Scribe_Values.Look(ref allowAnimals, "allowAnimals", false);
            Scribe_Values.Look(ref useRealItemValue, "useRealItemValue", true);
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

            list.GapLine(6f);
            list.CheckboxLabeled(
                "Preco de venda pelo valor real do item",
                ref useRealItemValue,
                "LIGADO (recomendado): a venda usa o valor real de cada item - material (placo, ouro...), "
                + "qualidade (ruim ate lendaria) e dano, igual a uma caravana. Vende primeiro os itens "
                + "mais baratos, preservando seu equipamento bom.\n\n"
                + "DESLIGADO: comportamento antigo - paga o valor base do tipo do item, ignorando material, "
                + "qualidade e dano (uma espada de placo lendaria vale o mesmo que uma de madeira ruim), "
                + "e consome primeiro as pilhas maiores."
            );

            list.GapLine(6f);
            list.CheckboxLabeled(
                "Allow buying animals",
                ref allowAnimals,
                "If enabled, an Animals tab appears in the catalog. Purchased animals arrive tamed and join your colony."
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