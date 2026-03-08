using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Ramazon
{
    public class MainTabWindow_Ramazon : MainTabWindow
    {
        // UI state
        private string search = "";
        private Vector2 scroll;
        private Vector2 cartScroll;
        private ThingDef selected;
        private int sortIdx = 0; // 0: Nome, 1: Valor, 2: Categoria
        private int page = 0;
        private const int PerPage = 18; // 6x3

        // filtros com contadores
        private bool fMaterials = true;
        private bool fMedicine = true;
        private bool fComponents = true;
        private bool fBionics = true;
        private bool fChips = true;
        private bool fOther = true;

        // modo (Buy / Sell)
        private enum ShopMode { Buy, Sell }
        private ShopMode mode = ShopMode.Buy;

        // Carrinho: def -> quantidade
        private readonly Dictionary<ThingDef, int> cart = new();

        // >>> NOVO: buffer de texto por item para edição manual no carrinho
        private readonly Dictionary<ThingDef, string> cartQtyBuf = new();

        // Estoque para venda (modo Sell): def -> quantidade no mapa
        private Dictionary<ThingDef, int> sellableCounts = new();

        // Quantidade "planejada" por item no card de venda
        private readonly Dictionary<ThingDef, int> sellQty = new();
        private readonly Dictionary<ThingDef, string> sellQtyBuf = new();

        // Carrinho de animais (Buy mode)
        private readonly Dictionary<PawnKindDef, int> animalCart = new();
        private int catalogTab = 0; // 0=Items, 1=Animals
        private Vector2 animalScroll;
        private int animalPage = 0;
        private const int AnimalPerPage = 18;
        private Gender animalGenderFilter = Gender.None; // None = Any
        private int animalAgeFilter = 0; // 0=Any, 1=Young, 2=Adult

        // Cache para performance
        private List<ThingDef> cachedBuyItems;
        private List<ThingDef> cachedSellItems;
        private int lastUpdateTick;

        // Cores e estilos
        private static readonly Color BuyModeColor = new Color(0.2f, 0.8f, 0.2f, 0.8f);
        private static readonly Color SellModeColor = new Color(0.8f, 0.4f, 0.2f, 0.8f);
        private static readonly Color CartBackgroundColor = new Color(0.1f, 0.1f, 0.15f, 0.9f);

        public override Vector2 RequestedTabSize => new Vector2(1200f, 720f);

        public override void DoWindowContents(Rect inRect)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Widgets.Label(inRect, "No map loaded.");
                return;
            }

            var st = ModEntry.Settings;
            long silver = map.listerThings.ThingsOfDef(ThingDefOf.Silver).Sum(t => (long)t.stackCount);

            if (mode == ShopMode.Sell)
                sellableCounts = ComputeSellableCounts(map);

            if (cachedBuyItems == null || Find.TickManager.TicksGame - lastUpdateTick > 300)
            {
                RefreshCache(map, st);
                lastUpdateTick = Find.TickManager.TicksGame;
            }

            float leftW = 260f;
            float rightW = 340f;
            var left = new Rect(inRect.x, inRect.y, leftW, inRect.height);
            var right = new Rect(inRect.xMax - rightW, inRect.y, rightW, inRect.height);
            var mid = new Rect(left.xMax + 8f, inRect.y, inRect.width - leftW - rightW - 16f, inRect.height);

            DrawSidebarFilters(left, st, silver);
            DrawCatalog(mid, map, st);
            DrawCart(right, map, st, silver);
        }

        // ---------- SIDEBAR ----------
        private void DrawSidebarFilters(Rect rect, RamazonSettings st, long silver)
        {
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(8f);
            var list = new Listing_Standard();
            list.Begin(inner);

            Text.Font = GameFont.Medium;
            var headerRect = list.GetRect(32f);
            GUI.color = mode == ShopMode.Buy ? BuyModeColor : SellModeColor;
            Widgets.Label(headerRect, "RAMAZON");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            list.GapLine();

            list.Label("Mode");
            var modeRect = list.GetRect(48f);
            var buyRect = new Rect(modeRect.x, modeRect.y, modeRect.width, 22f);
            var sellRect = new Rect(modeRect.x, modeRect.y + 24f, modeRect.width, 22f);

            GUI.color = mode == ShopMode.Buy ? BuyModeColor : Color.gray;
            if (Widgets.RadioButtonLabeled(buyRect, "Buy (pay MV × multiplier)", mode == ShopMode.Buy))
            {
                if (mode != ShopMode.Buy)
                {
                    mode = ShopMode.Buy;
                    cart.Clear();
                    cartQtyBuf.Clear();
                    animalCart.Clear();
                    SoundStarter.PlayOneShot(SoundDefOf.Click, SoundInfo.OnCamera());
                }
            }

            GUI.color = mode == ShopMode.Sell ? SellModeColor : Color.gray;
            if (Widgets.RadioButtonLabeled(sellRect, "Sell (receive MV - fee)", mode == ShopMode.Sell))
            {
                if (mode != ShopMode.Sell)
                {
                    mode = ShopMode.Sell;
                    cart.Clear();
                    cartQtyBuf.Clear();
                    animalCart.Clear();
                    catalogTab = 0;
                    sellableCounts = ComputeSellableCounts(Find.CurrentMap);
                    cachedSellItems = sellableCounts?.Keys.ToList() ?? new List<ThingDef>();
                    page = 0;
                    SoundStarter.PlayOneShot(SoundDefOf.Click, SoundInfo.OnCamera());
                }
            }
            GUI.color = Color.white;
            list.GapLine();

            list.Label("Search");
            var searchRect = list.GetRect(26f);
            var newSearch = Widgets.TextField(searchRect, search ?? "");
            if (newSearch != search) { search = newSearch; page = 0; }
            list.Gap(6f);

            list.Label("Sort by");
            var sorts = new[] { "Name (A→Z)", "Market Value (↓)", "Category" };
            for (int i = 0; i < sorts.Length; i++)
                if (Widgets.RadioButtonLabeled(list.GetRect(20f), sorts[i], sortIdx == i))
                { sortIdx = i; page = 0; SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }

            list.GapLine();

            list.Label("Categories");
            var itemCounts = GetCategoryCounts();
            DrawFilterCheckbox(list, "Materials", ref fMaterials, itemCounts["Materials"]);
            DrawFilterCheckbox(list, "Medicine",  ref fMedicine,  itemCounts["Medicine"]);
            DrawFilterCheckbox(list, "Components",ref fComponents,itemCounts["Components"]);
            DrawFilterCheckbox(list, "Bionics",   ref fBionics,   itemCounts["Bionics"]);
            DrawFilterCheckbox(list, "Chips/Ultra", ref fChips,   itemCounts["Chips"]);
            DrawFilterCheckbox(list, "Other",     ref fOther,     itemCounts["Other"]);

            list.GapLine();

            var infoRect = list.GetRect(90f);
            Widgets.DrawMenuSection(infoRect);
            var infoInner = infoRect.ContractedBy(4f);
            if (mode == ShopMode.Buy)
                Widgets.Label(new Rect(infoInner.x, infoInner.y, infoInner.width, 22f), $"Price: {st.priceMultiplier:0.00}× market value");
            else
            {
                Widgets.Label(new Rect(infoInner.x, infoInner.y, infoInner.width, 22f), $"Fee: {st.sellTaxPercent:0}%");
                Widgets.Label(new Rect(infoInner.x, infoInner.y + 22f, infoInner.width, 22f), $"You receive: {(100f - st.sellTaxPercent):0}%");
            }

            GUI.color = Color.yellow;
            Widgets.Label(new Rect(infoInner.x, infoInner.yMax - 22f, infoInner.width, 22f), $"Silver: {FormatNumber(silver)}");
            GUI.color = Color.white;

            list.Gap(84f);
            list.GapLine();

            list.CheckboxLabeled("Allow stuffables (experimental)", ref st.showStuffables);
            if (st.showStuffables)
            {
                list.Label("Default material:");
                var stuffRect = list.GetRect(24f);
                st.defaultStuffDefName = Widgets.TextField(stuffRect, st.defaultStuffDefName ?? "Steel");
            }

            if (list.ButtonText("Clear cart"))
            {
                cart.Clear();
                cartQtyBuf.Clear();
                animalCart.Clear();
                SoundStarter.PlayOneShot(SoundDefOf.CancelMode, SoundInfo.OnCamera());
            }

            list.End();
        }

        private void DrawFilterCheckbox(Listing_Standard list, string label, ref bool value, int count)
        {
            var rect = list.GetRect(22f);
            var checkRect = new Rect(rect.x, rect.y, 24f, rect.height);
            var labelRect = new Rect(rect.x + 26f, rect.y, rect.width - 60f, rect.height);
            var countRect = new Rect(rect.xMax - 32f, rect.y, 32f, rect.height);

            Widgets.Checkbox(checkRect.x, checkRect.y, ref value);
            Widgets.Label(labelRect, label);
            GUI.color = Color.gray;
            Widgets.Label(countRect, count.ToString());
            GUI.color = Color.white;
        }

        private Dictionary<string, int> GetCategoryCounts()
        {
            var items = (mode == ShopMode.Buy) ? cachedBuyItems : cachedSellItems;
            var counts = new Dictionary<string, int>
            {
                ["Materials"] = 0, ["Medicine"] = 0, ["Components"] = 0,
                ["Bionics"] = 0, ["Chips"] = 0, ["Other"] = 0
            };
            if (items == null) return counts;

            foreach (var def in items)
            {
                if (IsMaterial(def)) counts["Materials"]++;
                else if (IsMedicine(def)) counts["Medicine"]++;
                else if (IsComponent(def)) counts["Components"]++;
                else if (IsBionic(def)) counts["Bionics"]++;
                else if (IsChip(def)) counts["Chips"]++;
                else counts["Other"]++;
            }
            return counts;
        }

        // ---------- CATÁLOGO ----------
        private void DrawCatalog(Rect rect, Map map, RamazonSettings st)
        {
            Widgets.DrawMenuSection(rect);
            var top = rect.TopPartPixels(40f).ContractedBy(6f);
            var body = new Rect(rect.x, rect.y + 40f, rect.width, rect.height - 40f).ContractedBy(6f);

            // Tab buttons (Buy mode + animals enabled)
            float tabOffset = 0f;
            if (st.allowAnimals && mode == ShopMode.Buy)
            {
                tabOffset = 184f;
                var tabItemsRect = new Rect(top.x, top.y + 2f, 88f, 24f);
                var tabAnimRect  = new Rect(top.x + 92f, top.y + 2f, 88f, 24f);
                GUI.color = catalogTab == 0 ? BuyModeColor : new Color(0.6f, 0.6f, 0.6f);
                if (Widgets.ButtonText(tabItemsRect, "Items"))   { catalogTab = 0; page = 0; }
                GUI.color = catalogTab == 1 ? BuyModeColor : new Color(0.6f, 0.6f, 0.6f);
                if (Widgets.ButtonText(tabAnimRect,  "Animals")) { catalogTab = 1; animalPage = 0; }
                GUI.color = Color.white;

                if (catalogTab == 1)
                {
                    var animals = GetBuyableAnimals();
                    int totalAnimalPages = Math.Max(1, Mathf.CeilToInt(animals.Count / (float)AnimalPerPage));
                    animalPage = Mathf.Clamp(animalPage, 0, totalAnimalPages - 1);

                    var aHeaderLeft  = new Rect(top.x + tabOffset, top.y, top.width - 200f - tabOffset, 32f);
                    var aHeaderRight = new Rect(top.xMax - 200f, top.y, 200f, 32f);
                    Text.Font = GameFont.Medium;
                    Widgets.Label(aHeaderLeft, $"{animals.Count} animals • Page {animalPage + 1}/{totalAnimalPages}");
                    Text.Font = GameFont.Small;

                    var aPrev = new Rect(aHeaderRight.x, aHeaderRight.y, 60f, 28f);
                    var aNext = new Rect(aHeaderRight.x + 65f, aHeaderRight.y, 60f, 28f);
                    if (Widgets.ButtonText(aPrev, "< Prev", true, true, animalPage > 0))
                    { animalPage = Math.Max(0, animalPage - 1); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }
                    if (Widgets.ButtonText(aNext, "Next >", true, true, animalPage < totalAnimalPages - 1))
                    { animalPage = Math.Min(totalAnimalPages - 1, animalPage + 1); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }

                    // Filter row (second header line)
                    float fy = rect.y + 40f + 4f;
                    float fx = rect.x + 8f;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(fx, fy, 34f, 22f), "Sex:"); fx += 36f;
                    GUI.color = animalGenderFilter == Gender.None   ? BuyModeColor : new Color(0.55f,0.55f,0.55f);
                    if (Widgets.ButtonText(new Rect(fx, fy, 42f, 22f), "Any"))    { animalGenderFilter = Gender.None;   animalPage = 0; } fx += 44f;
                    GUI.color = animalGenderFilter == Gender.Male   ? BuyModeColor : new Color(0.55f,0.55f,0.55f);
                    if (Widgets.ButtonText(new Rect(fx, fy, 54f, 22f), "♂ Male")) { animalGenderFilter = Gender.Male;   animalPage = 0; } fx += 56f;
                    GUI.color = animalGenderFilter == Gender.Female ? BuyModeColor : new Color(0.55f,0.55f,0.55f);
                    if (Widgets.ButtonText(new Rect(fx, fy, 66f, 22f), "♀ Female")) { animalGenderFilter = Gender.Female; animalPage = 0; } fx += 76f;
                    GUI.color = Color.white;
                    Widgets.Label(new Rect(fx, fy, 34f, 22f), "Age:"); fx += 36f;
                    GUI.color = animalAgeFilter == 0 ? BuyModeColor : new Color(0.55f,0.55f,0.55f);
                    if (Widgets.ButtonText(new Rect(fx, fy, 42f, 22f), "Any"))   { animalAgeFilter = 0; animalPage = 0; } fx += 44f;
                    GUI.color = animalAgeFilter == 1 ? BuyModeColor : new Color(0.55f,0.55f,0.55f);
                    if (Widgets.ButtonText(new Rect(fx, fy, 52f, 22f), "Young")) { animalAgeFilter = 1; animalPage = 0; } fx += 54f;
                    GUI.color = animalAgeFilter == 2 ? BuyModeColor : new Color(0.55f,0.55f,0.55f);
                    if (Widgets.ButtonText(new Rect(fx, fy, 52f, 22f), "Adult")) { animalAgeFilter = 2; animalPage = 0; }
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;

                    var animalBody = new Rect(rect.x, rect.y + 80f, rect.width, rect.height - 80f).ContractedBy(6f);
                    DrawAnimalCatalog(animalBody, animals, st);
                    return;
                }
            }
            else { catalogTab = 0; }

            var filtered = (mode == ShopMode.Buy) ? QueryItems_Buy(st) : QueryItems_Sell(st);

            int totalPages = Math.Max(1, Mathf.CeilToInt(filtered.Count / (float)PerPage));
            page = Mathf.Clamp(page, 0, totalPages - 1);

            var headerLeft = new Rect(top.x + tabOffset, top.y, top.width - 200f - tabOffset, 32f);
            var headerRight = new Rect(top.xMax - 200f, top.y, 200f, 32f);

            Text.Font = GameFont.Medium;
            Widgets.Label(headerLeft, $"{filtered.Count} items • Page {page + 1}/{totalPages}");
            Text.Font = GameFont.Small;

            var prevBtn = new Rect(headerRight.x, headerRight.y, 60f, 28f);
            var nextBtn = new Rect(headerRight.x + 65f, headerRight.y, 60f, 28f);
            var jumpBtn = new Rect(headerRight.x + 130f, headerRight.y, 65f, 28f);

            if (Widgets.ButtonText(prevBtn, "< Prev", true, true, page > 0))
            { page = Math.Max(0, page - 1); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }

            if (Widgets.ButtonText(nextBtn, "Next >", true, true, page < totalPages - 1))
            { page = Math.Min(totalPages - 1, page + 1); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }

            if (Widgets.ButtonText(jumpBtn, "Jump", true, true, totalPages > 1))
            { page = 0; SoundStarter.PlayOneShot(SoundDefOf.Click, SoundInfo.OnCamera()); }

            var grid = new Listing_Grid();
            int columns = Mathf.Max(1, (int)(body.width / 268f));
            grid.Begin(body, ref scroll, cellW: 260f, cellH: 125f, hGap: 8f, vGap: 8f, columns: columns);

            int start = page * PerPage;
            int end = Mathf.Min(filtered.Count, start + PerPage);

            for (int i = start; i < end; i++)
            {
                var def = filtered[i];
                var cell = grid.NextCell();
                if (mode == ShopMode.Buy) DrawItemCard_Buy(cell, def, st);
                else DrawItemCard_Sell(cell, def, st, sellableCounts.TryGetValue(def, out var have) ? have : 0);
            }
            grid.End();
        }

        private void DrawItemCard_Buy(Rect cell, ThingDef def, RamazonSettings st)
        {
            bool hovered = Mouse.IsOver(cell);
            if (hovered) { Widgets.DrawHighlight(cell); GUI.color = new Color(1f,1f,1f,0.1f); Widgets.DrawBox(cell); GUI.color = Color.white; }
            else { Widgets.DrawLightHighlight(cell); Widgets.DrawBox(cell); }

            var iconRect = new Rect(cell.x + 8, cell.y + 8, 52, 52);
            if (def.uiIcon != null) { Widgets.ThingIcon(iconRect, def); Widgets.DrawBox(iconRect); }

            var nameRect = new Rect(iconRect.xMax + 8, cell.y + 8, cell.width - 76, 22);
            var name = (def.label ?? def.defName).CapitalizeFirst();
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(nameRect, name.Truncate(nameRect.width));
            TooltipHandler.TipRegion(cell, name + (!string.IsNullOrEmpty(def.description) ? "\n\n" + def.description : ""));

            var categoryRect = new Rect(iconRect.xMax + 8, cell.y + 30, 80, 16);
            GUI.color = GetCategoryColor(def);
            Text.Font = GameFont.Tiny; Widgets.Label(categoryRect, GetCategoryTag(def));
            GUI.color = Color.white; Text.Font = GameFont.Small;

            var priceRect = new Rect(iconRect.xMax + 8, cell.y + 48, cell.width - 76, 20);
            var cost = Math.Ceiling(def.BaseMarketValue * st.priceMultiplier);
            Widgets.Label(priceRect, $"MV: {def.BaseMarketValue:0.##} -> {FormatNumber((long)cost)}");
            Widgets.InfoCardButton(cell.x + 8f, cell.yMax - 54f, def);

            var btnRect = new Rect(cell.xMax - 90, cell.yMax - 30, 82, 26);
            var btnColor = IsBuyable(def, st, out string reason) ? BuyModeColor : Color.gray;
            GUI.color = btnColor;
            if (Widgets.ButtonText(btnRect, "+ Cart"))
            {
                if (IsBuyable(def, st, out reason))
                {
                    cart.TryGetValue(def, out var q);
                    cart[def] = q + 1;
                    cartQtyBuf[def] = cart[def].ToString(); // <<< mantém buffer em sincronia
                    selected = def;
                    SoundStarter.PlayOneShot(SoundDefOf.Tick_High, SoundInfo.OnCamera());
                }
                else Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
            }
            GUI.color = Color.white;

            if (cart.ContainsKey(def))
            {
                var qtyRect = new Rect(cell.xMax - 25, cell.y + 5, 20, 18);
                GUI.color = Color.green;
                Widgets.DrawBoxSolid(qtyRect, Color.black);
                Widgets.Label(qtyRect, cart[def].ToString());
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void DrawItemCard_Sell(Rect cell, ThingDef def, RamazonSettings st, int have)
        {
            bool hovered = Mouse.IsOver(cell);
            if (hovered) { Widgets.DrawHighlight(cell); GUI.color = SellModeColor * 0.3f; Widgets.DrawBoxSolid(cell, GUI.color); GUI.color = Color.white; }
            else Widgets.DrawLightHighlight(cell);
            Widgets.DrawBox(cell);

            var iconRect = new Rect(cell.x + 8, cell.y + 8, 48, 48);
            if (def.uiIcon != null) Widgets.ThingIcon(iconRect, def);

            var nameRect = new Rect(iconRect.xMax + 8, cell.y + 8, cell.width - 70, 18);
            Text.Font = GameFont.Small;
            var sellItemName = (def.label ?? def.defName).CapitalizeFirst();
            Widgets.Label(nameRect, sellItemName.Truncate(nameRect.width));
            TooltipHandler.TipRegion(cell, sellItemName + (!string.IsNullOrEmpty(def.description) ? "\n\n" + def.description : ""));

            float unitMV = Math.Max(0f, def.BaseMarketValue);
            float receivePct = (100f - st.sellTaxPercent) / 100f;

            var stockRect = new Rect(iconRect.xMax + 8, cell.y + 28, cell.width - 100, 16);
            Text.Font = GameFont.Tiny; Widgets.Label(stockRect, $"Stock: {FormatNumber(have)}");
            Widgets.InfoCardButton(cell.xMax - 28f, cell.y + 28f, def);

            var priceRect = new Rect(iconRect.xMax + 8, cell.y + 44, cell.width - 100, 16);
            Widgets.Label(priceRect, $"MV: {unitMV:0.##} -> {Math.Floor(unitMV * receivePct):0}");
            Text.Font = GameFont.Small;

            if (!sellQty.TryGetValue(def, out var q) || q <= 0) q = 1;
            sellQty[def] = q;
            if (!sellQtyBuf.ContainsKey(def)) sellQtyBuf[def] = q.ToString();

            cart.TryGetValue(def, out var already);
            int canAddMax = Math.Max(0, have - already);
            q = Mathf.Clamp(q, 1, Math.Max(1, canAddMax));
            sellQty[def] = q;

            float btnY1 = cell.yMax - 52f;
            float btnY2 = cell.yMax - 26f;
            float btnH = 22f;
            float startX = cell.x + 8f;

            var btn1 = new Rect(startX, btnY1, 28f, btnH);
            var btn2 = new Rect(startX + 32f, btnY1, 24f, btnH);
            var qtyField = new Rect(startX + 60f, btnY1, 40f, btnH);
            var btn3 = new Rect(startX + 104f, btnY1, 24f, btnH);
            var btn4 = new Rect(startX + 132f, btnY1, 28f, btnH);
            var maxBtn = new Rect(startX + 164f, btnY1, 32f, btnH);

            GUI.color = Color.cyan;
            if (Widgets.ButtonText(btn1, "<<")) { sellQty[def] = Mathf.Clamp(q - 10, 1, Math.Max(1, canAddMax)); sellQtyBuf[def] = sellQty[def].ToString(); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }
            if (Widgets.ButtonText(btn2, "<"))  { sellQty[def] = Mathf.Clamp(q - 1 , 1, Math.Max(1, canAddMax)); sellQtyBuf[def] = sellQty[def].ToString(); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }
            GUI.color = Color.white;

            int tmp = q; string tmpStr = sellQtyBuf[def];
            GUI.color = new Color(0.2f, 0.2f, 0.2f); Widgets.DrawBoxSolid(qtyField, GUI.color); GUI.color = Color.white;
            Widgets.TextFieldNumeric<int>(qtyField, ref tmp, ref tmpStr, 1, Math.Max(1, canAddMax));
            sellQtyBuf[def] = tmpStr; tmp = Mathf.Clamp(tmp, 1, Math.Max(1, canAddMax)); sellQty[def] = tmp;

            GUI.color = Color.cyan;
            if (Widgets.ButtonText(btn3, ">"))  { sellQty[def] = Mathf.Clamp(q + 1 , 1, Math.Max(1, canAddMax)); sellQtyBuf[def] = sellQty[def].ToString(); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }
            if (Widgets.ButtonText(btn4, ">>")) { sellQty[def] = Mathf.Clamp(q + 10, 1, Math.Max(1, canAddMax)); sellQtyBuf[def] = sellQty[def].ToString(); SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); }
            GUI.color = Color.yellow;
            if (Widgets.ButtonText(maxBtn, "Max")) { sellQty[def] = Math.Max(1, canAddMax); sellQtyBuf[def] = sellQty[def].ToString(); SoundStarter.PlayOneShot(SoundDefOf.Tick_High, SoundInfo.OnCamera()); }
            GUI.color = Color.white;

            var addRect = new Rect(startX, btnY2, cell.width - 20f, btnH);
            GUI.color = canAddMax > 0 ? SellModeColor : Color.gray;
            string addText = canAddMax > 0 ? $"Add {sellQty[def]} to cart" : "No stock";
            if (Widgets.ButtonText(addRect, addText))
            {
                if (canAddMax <= 0) { Messages.Message("No stock available to sell.", MessageTypeDefOf.RejectInput, false); return; }
                int add = Mathf.Clamp(sellQty[def], 1, canAddMax);
                cart[def] = already + add;
                cartQtyBuf[def] = cart[def].ToString(); // mantém buffer em sincronia
                SoundStarter.PlayOneShot(SoundDefOf.Tick_High, SoundInfo.OnCamera());
            }
            GUI.color = Color.white;
        }

        // ---------- ANIMAIS ----------
        private void DrawAnimalCatalog(Rect body, List<PawnKindDef> animals, RamazonSettings st)
        {
            var grid = new Listing_Grid();
            int columns = Mathf.Max(1, (int)(body.width / 268f));
            grid.Begin(body, ref animalScroll, cellW: 260f, cellH: 125f, hGap: 8f, vGap: 8f, columns: columns);
            int start = animalPage * AnimalPerPage;
            int end = Mathf.Min(animals.Count, start + AnimalPerPage);
            for (int i = start; i < end; i++)
                DrawAnimalCard(grid.NextCell(), animals[i], st);
            grid.End();
        }

        private void DrawAnimalCard(Rect cell, PawnKindDef kindDef, RamazonSettings st)
        {
            bool hovered = Mouse.IsOver(cell);
            if (hovered) { Widgets.DrawHighlight(cell); GUI.color = new Color(1f, 1f, 1f, 0.1f); Widgets.DrawBox(cell); GUI.color = Color.white; }
            else { Widgets.DrawLightHighlight(cell); Widgets.DrawBox(cell); }

            var iconRect = new Rect(cell.x + 8, cell.y + 8, 52, 52);
            if (kindDef.race?.uiIcon != null) { Widgets.ThingIcon(iconRect, kindDef.race); Widgets.DrawBox(iconRect); }

            var nameRect = new Rect(iconRect.xMax + 8, cell.y + 8, cell.width - 76, 22);
            var name = (kindDef.label ?? kindDef.defName).CapitalizeFirst();
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(nameRect, name.Truncate(nameRect.width));
            string tip = name + (!string.IsNullOrEmpty(kindDef.race?.description) ? "\n\n" + kindDef.race.description : "");
            TooltipHandler.TipRegion(cell, tip);

            var tagRect = new Rect(iconRect.xMax + 8, cell.y + 30, 80, 16);
            GUI.color = new Color(0.4f, 0.85f, 0.5f);
            Text.Font = GameFont.Tiny;
            string tag = kindDef.RaceProps.predator ? "PREDATOR" : (kindDef.RaceProps.herdAnimal ? "HERD" : "PET");
            Widgets.Label(tagRect, tag);
            GUI.color = Color.white; Text.Font = GameFont.Small;

            var priceRect = new Rect(iconRect.xMax + 8, cell.y + 48, cell.width - 76, 20);
            var cost = Math.Ceiling((kindDef.race?.BaseMarketValue ?? 0f) * st.priceMultiplier);
            Widgets.Label(priceRect, $"MV: {kindDef.race?.BaseMarketValue:0.##} -> {FormatNumber((long)cost)}");

            if (kindDef.race != null) Widgets.InfoCardButton(cell.x + 8f, cell.yMax - 54f, kindDef.race);

            var btnRect = new Rect(cell.xMax - 90, cell.yMax - 30, 82, 26);
            GUI.color = BuyModeColor;
            if (Widgets.ButtonText(btnRect, "+ Cart"))
            {
                animalCart.TryGetValue(kindDef, out var q);
                animalCart[kindDef] = q + 1;
                SoundStarter.PlayOneShot(SoundDefOf.Tick_High, SoundInfo.OnCamera());
            }
            GUI.color = Color.white;

            if (animalCart.ContainsKey(kindDef))
            {
                var qtyRect = new Rect(cell.xMax - 25, cell.y + 5, 20, 18);
                GUI.color = Color.green;
                Widgets.DrawBoxSolid(qtyRect, Color.black);
                Widgets.Label(qtyRect, animalCart[kindDef].ToString());
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private List<PawnKindDef> GetBuyableAnimals()
        {
            var animals = DefDatabase<PawnKindDef>.AllDefsListForReading
                .Where(d => d.race != null && d.race.race != null && d.race.race.Animal && d.race.BaseMarketValue > 0f)
                .ToList();

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim().ToLowerInvariant();
                animals = animals.Where(d =>
                    (d.label ?? d.defName).ToLowerInvariant().Contains(s) ||
                    d.defName.ToLowerInvariant().Contains(s)).ToList();
            }
            return animals.OrderBy(d => d.label ?? d.defName).ToList();
        }

        // ---------- CARRINHO ----------
        private void DrawCart(Rect rect, Map map, RamazonSettings st, long silver)
        {
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(8f);

            var headerRect = new Rect(inner.x, inner.y, inner.width, 32f);
            GUI.color = CartBackgroundColor;
            Widgets.DrawBoxSolid(headerRect, GUI.color);
            GUI.color = Color.white;

            Text.Font = GameFont.Medium;
            var titleRect = new Rect(headerRect.x + 8, headerRect.y, headerRect.width - 80, headerRect.height);
            var cartTitle = (mode == ShopMode.Buy) ? "Cart" : "Sell Cart";
            Widgets.Label(titleRect, $"{cartTitle} ({cart.Count})");

            var clearRect = new Rect(headerRect.xMax - 68, headerRect.y + 4, 60, 24);
            if (Widgets.ButtonText(clearRect, "Clear") && (cart.Count > 0 || animalCart.Count > 0))
            {
                cart.Clear();
                cartQtyBuf.Clear();
                animalCart.Clear();
                SoundStarter.PlayOneShot(SoundDefOf.CancelMode, SoundInfo.OnCamera());
            }
            Text.Font = GameFont.Small;

            var listRect = new Rect(inner.x, inner.y + 36f, inner.width, inner.height - 190f);

            // Pre-calc subtotal including animal cart
            double subtotalMV = 0;
            foreach (var kv in cart)
                subtotalMV += (double)Math.Max(0f, kv.Key.BaseMarketValue) * kv.Value;
            if (mode == ShopMode.Buy && st.allowAnimals)
                foreach (var kv in animalCart)
                    subtotalMV += (double)Math.Max(0f, kv.Key.race?.BaseMarketValue ?? 0f) * kv.Value;

            int animalRowCount = (mode == ShopMode.Buy && st.allowAnimals) ? animalCart.Count : 0;
            bool hasBothTypes = cart.Count > 0 && animalRowCount > 0;
            var contentH = (cart.Count + animalRowCount) * 68f + (hasBothTypes ? 24f : 0f) + 8f;
            var viewRect = new Rect(listRect.x, listRect.y, listRect.width - 16f, Math.Max(contentH, listRect.height));

            Widgets.BeginScrollView(listRect, ref cartScroll, viewRect);
            float y = viewRect.y + 4f;
            int itemIndex = 0;

            foreach (var kv in cart.ToList())
            {
                var def = kv.Key;
                int qty = kv.Value;
                float unitMV = Math.Max(0f, def.BaseMarketValue);

                var row = new Rect(viewRect.x, y, viewRect.width, 64f);

                if (itemIndex % 2 == 0) { GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.3f); Widgets.DrawBoxSolid(row, GUI.color); }
                GUI.color = Color.white;
                Widgets.DrawBox(row);

                var icon = new Rect(row.x + 4, row.y + 8, 48, 48);
                if (def.uiIcon != null) Widgets.ThingIcon(icon, def);

                var nameRect = new Rect(icon.xMax + 8, row.y + 6, row.width - 160, 20);
                Widgets.Label(nameRect, (def.label ?? def.defName).CapitalizeFirst().Truncate(nameRect.width - 20));

                var infoRect = new Rect(icon.xMax + 8, row.y + 26, row.width - 160, 16);
                Text.Font = GameFont.Tiny;
                Widgets.Label(infoRect, $"MV: {unitMV:0.##} × {qty} = {(unitMV * qty):0.##}");
                Text.Font = GameFont.Small;

                var minusRect = new Rect(row.xMax - 140, row.y + 20, 28, 24);
                var qtyRect   = new Rect(row.xMax - 110, row.y + 20, 50, 24);
                var plusRect  = new Rect(row.xMax - 58,  row.y + 20, 28, 24);

                // --- botão "-" (sincroniza buffer) ---
                if (Widgets.ButtonText(minusRect, "-") && qty > 1)
                {
                    cart[def] = qty - 1;
                    cartQtyBuf[def] = cart[def].ToString();
                    SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera());
                    qty = cart[def];
                }

                // --- campo numérico editável ---
                int tmpQty = qty;
                if (!cartQtyBuf.TryGetValue(def, out var buf) || string.IsNullOrEmpty(buf))
                    buf = qty.ToString();

                int maxAllowed = int.MaxValue;
                if (mode == ShopMode.Sell)
                {
                    int have = sellableCounts.TryGetValue(def, out var h) ? h : 0;
                    maxAllowed = Math.Max(1, have);
                }

                GUI.color = new Color(0.2f, 0.2f, 0.2f);
                Widgets.DrawBoxSolid(qtyRect, GUI.color);
                GUI.color = Color.white;

                Widgets.TextFieldNumeric<int>(qtyRect, ref tmpQty, ref buf, 1, maxAllowed);
                cartQtyBuf[def] = buf;
                tmpQty = Mathf.Clamp(tmpQty, 1, maxAllowed);

                if (tmpQty != qty)
                {
                    cart[def] = tmpQty;
                    qty = tmpQty;
                }

                // se digitar 0, remove
                if (qty <= 0)
                {
                    cart.Remove(def);
                    cartQtyBuf.Remove(def);
                    continue;
                }

                // --- botão "+" (sincroniza buffer e respeita estoque no Sell) ---
                bool canAdd = true;
                if (mode == ShopMode.Sell)
                {
                    int have = sellableCounts.TryGetValue(def, out var h) ? h : 0;
                    if (qty + 1 > have) { canAdd = false; Messages.Message("Cart would exceed available stock.", MessageTypeDefOf.RejectInput, false); }
                }
                if (Widgets.ButtonText(plusRect, "+") && canAdd)
                {
                    cart[def] = qty + 1;
                    cartQtyBuf[def] = cart[def].ToString();
                    SoundStarter.PlayOneShot(SoundDefOf.Tick_High, SoundInfo.OnCamera());
                }

                var remRect = new Rect(row.xMax - 26, row.y + 4, 22, 22);
                GUI.color = Color.red;
                if (Widgets.ButtonImage(remRect, TexButton.CloseXSmall))
                {
                    cart.Remove(def);
                    cartQtyBuf.Remove(def);
                    SoundStarter.PlayOneShot(SoundDefOf.CancelMode, SoundInfo.OnCamera());
                    continue;
                }
                GUI.color = Color.white;

                y += 68f;
                itemIndex++;
            }

            // Animal cart rows
            if (mode == ShopMode.Buy && st.allowAnimals && animalCart.Count > 0)
            {
                if (cart.Count > 0)
                {
                    GUI.color = new Color(0.2f, 0.4f, 0.2f, 0.5f);
                    Widgets.DrawBoxSolid(new Rect(viewRect.x, y, viewRect.width, 20f), GUI.color);
                    GUI.color = new Color(0.5f, 1f, 0.5f);
                    Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(new Rect(viewRect.x, y + 2, viewRect.width, 16), "— Animals —");
                    Text.Anchor = TextAnchor.UpperLeft; Text.Font = GameFont.Small; GUI.color = Color.white;
                    y += 24f;
                }
                foreach (var kv in animalCart.ToList())
                {
                    var kindDef = kv.Key;
                    int qty = kv.Value;
                    float unitMV = Math.Max(0f, kindDef.race?.BaseMarketValue ?? 0f);

                    var row = new Rect(viewRect.x, y, viewRect.width, 64f);
                    GUI.color = new Color(0.05f, 0.15f, 0.05f, 0.5f); Widgets.DrawBoxSolid(row, GUI.color);
                    GUI.color = Color.white; Widgets.DrawBox(row);

                    var aIcon = new Rect(row.x + 4, row.y + 8, 48, 48);
                    if (kindDef.race?.uiIcon != null) Widgets.ThingIcon(aIcon, kindDef.race);

                    var aNameRect = new Rect(aIcon.xMax + 8, row.y + 6, row.width - 160, 20);
                    Widgets.Label(aNameRect, (kindDef.label ?? kindDef.defName).CapitalizeFirst().Truncate(aNameRect.width - 20));

                    var aInfoRect = new Rect(aIcon.xMax + 8, row.y + 26, row.width - 160, 16);
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(aInfoRect, $"MV: {unitMV:0.##} × {qty} = {(unitMV * qty):0.##}");
                    Text.Font = GameFont.Small;

                    var aMinusRect = new Rect(row.xMax - 140, row.y + 20, 28, 24);
                    var aQtyRect   = new Rect(row.xMax - 110, row.y + 20, 50, 24);
                    var aPlusRect  = new Rect(row.xMax - 58,  row.y + 20, 28, 24);

                    if (Widgets.ButtonText(aMinusRect, "-") && qty > 1)
                    { animalCart[kindDef] = qty - 1; SoundStarter.PlayOneShot(SoundDefOf.Tick_Low, SoundInfo.OnCamera()); qty = animalCart[kindDef]; }

                    GUI.color = new Color(0.2f, 0.2f, 0.2f); Widgets.DrawBoxSolid(aQtyRect, GUI.color); GUI.color = Color.white;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(aQtyRect, qty.ToString());
                    Text.Anchor = TextAnchor.UpperLeft;

                    if (Widgets.ButtonText(aPlusRect, "+"))
                    { animalCart[kindDef] = qty + 1; SoundStarter.PlayOneShot(SoundDefOf.Tick_High, SoundInfo.OnCamera()); }

                    var aRemRect = new Rect(row.xMax - 26, row.y + 4, 22, 22);
                    GUI.color = Color.red;
                    if (Widgets.ButtonImage(aRemRect, TexButton.CloseXSmall))
                    { animalCart.Remove(kindDef); SoundStarter.PlayOneShot(SoundDefOf.CancelMode, SoundInfo.OnCamera()); GUI.color = Color.white; y += 68f; continue; }
                    GUI.color = Color.white;
                    y += 68f;
                }
            }
            Widgets.EndScrollView();

            var summaryRect = new Rect(inner.x, inner.yMax - 150f, inner.width, 150f);
            GUI.color = CartBackgroundColor; Widgets.DrawBoxSolid(summaryRect, GUI.color); GUI.color = Color.white; Widgets.DrawBox(summaryRect);
            var summaryInner = summaryRect.ContractedBy(8f);

            if (mode == ShopMode.Buy)
            {
                double totalSilver = Math.Ceiling(subtotalMV * st.priceMultiplier);
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y, summaryInner.width, 22), $"Subtotal MV: {FormatNumber((long)subtotalMV)}");
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 24, summaryInner.width, 22), $"Multiplier: {st.priceMultiplier:0.00}x");

                Text.Font = GameFont.Medium;
                GUI.color = totalSilver <= silver ? Color.green : Color.red;
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 50, summaryInner.width, 28), $"Total: {FormatNumber((long)totalSilver)}");
                GUI.color = Color.white; Text.Font = GameFont.Small;

                if (totalSilver <= silver) { GUI.color = Color.green; Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 82, summaryInner.width, 22), "You can afford this purchase"); }
                else { GUI.color = Color.red; var shortage = totalSilver - silver; Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 82, summaryInner.width, 22), $"Need {FormatNumber((long)shortage)} more silver"); }
                GUI.color = Color.white;

                string reason2 = null;
                bool canCheckout = (cart.Count > 0 || animalCart.Count > 0) && totalSilver <= silver && AllCartItemsBuyable(st, out reason2);
                var buyBtnRect = new Rect(summaryInner.x, summaryInner.y + 108, summaryInner.width, 26);
                GUI.color = canCheckout ? BuyModeColor : Color.gray; Text.Font = GameFont.Medium;

                if (Widgets.ButtonText(buyBtnRect, "BUY NOW"))
                {
                    if (!canCheckout) { Messages.Message(reason2 ?? "Cannot complete purchase.", MessageTypeDefOf.RejectInput, false); SoundStarter.PlayOneShot(SoundDefOf.ClickReject, SoundInfo.OnCamera()); }
                    else if (ConsumeSilver(map, (int)totalSilver))
                    {
                        bool hasAnimals = st.allowAnimals && animalCart.Count > 0;
                        var dropSpot = DoSpawnCart(map, st);
                        if (hasAnimals)
                        {
                            var animalSpot = DropCellFinder.TradeDropSpot(map);
                            var animalThings = new List<Thing>();
                            Gender? fixedGender = animalGenderFilter != Gender.None ? (Gender?)animalGenderFilter : null;
                            DevelopmentalStage stages = animalAgeFilter == 1
                                ? (DevelopmentalStage.Newborn | DevelopmentalStage.Child)
                                : animalAgeFilter == 2 ? DevelopmentalStage.Adult
                                : (DevelopmentalStage.Newborn | DevelopmentalStage.Child | DevelopmentalStage.Adult);
                            foreach (var akv in animalCart)
                                for (int i = 0; i < akv.Value; i++)
                                {
                                    var req = new PawnGenerationRequest(akv.Key, Faction.OfPlayer, fixedGender: fixedGender);
                                    req.AllowedDevelopmentalStages = stages;
                                    animalThings.Add(PawnGenerator.GeneratePawn(req));
                                }
                            DropPodUtility.DropThingsNear(animalSpot, map, animalThings, 110, false, false, true);
                        }
                        cart.Clear(); cartQtyBuf.Clear(); animalCart.Clear();
                        string msg = hasAnimals
                            ? $"Purchase complete! Paid {FormatNumber((long)totalSilver)} silver. Drop pods incoming!"
                            : $"Purchase completed! Paid {FormatNumber((long)totalSilver)} silver. A drop pod is incoming!";
                        Messages.Message(msg, new LookTargets(new TargetInfo(dropSpot, map)), MessageTypeDefOf.PositiveEvent, false);
                        SoundStarter.PlayOneShot(SoundDefOf.ExecuteTrade, SoundInfo.OnCamera());
                    }
                    else { Messages.Message("Transaction failed - insufficient silver.", MessageTypeDefOf.RejectInput, false); SoundStarter.PlayOneShot(SoundDefOf.ClickReject, SoundInfo.OnCamera()); }
                }
                GUI.color = Color.white; Text.Font = GameFont.Small;
            }
            else
            {
                float receivePct = (100f - st.sellTaxPercent) / 100f;
                double totalGain = Math.Floor(subtotalMV * receivePct);
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y, summaryInner.width, 22), $"Subtotal MV: {FormatNumber((long)subtotalMV)}");
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 24, summaryInner.width, 22), $"Fee: {st.sellTaxPercent:0}% • You get: {(receivePct * 100f):0}%");

                Text.Font = GameFont.Medium; GUI.color = Color.yellow;
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 50, summaryInner.width, 28), $"Payout: {FormatNumber((long)totalGain)}");
                GUI.color = Color.white; Text.Font = GameFont.Small;

                bool stockOk = CartWithinStock();
                if (stockOk) { GUI.color = Color.green; Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 82, summaryInner.width, 22), "All items available in stock"); }
                else { GUI.color = Color.red; Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 82, summaryInner.width, 22), "Cart exceeds available stock"); }
                GUI.color = Color.white;

                bool canSell = cart.Count > 0 && stockOk;
                var sellBtnRect = new Rect(summaryInner.x, summaryInner.y + 108, summaryInner.width, 26);
                GUI.color = canSell ? SellModeColor : Color.gray; Text.Font = GameFont.Medium;

                if (Widgets.ButtonText(sellBtnRect, "SELL NOW"))
                {
                    if (!canSell) { Messages.Message("Cannot sell - check stock availability.", MessageTypeDefOf.RejectInput, false); SoundStarter.PlayOneShot(SoundDefOf.ClickReject, SoundInfo.OnCamera()); }
                    else
                    {
                        RemoveSoldItemsFromMap(map);
                        var silverSpot = GiveSilver(map, (int)totalGain);
                        cart.Clear(); cartQtyBuf.Clear();
                        sellableCounts = ComputeSellableCounts(map);
                        Messages.Message($"Sale completed! A drop pod with {FormatNumber((long)totalGain)} silver is incoming!", new LookTargets(new TargetInfo(silverSpot, map)), MessageTypeDefOf.PositiveEvent, false);
                        SoundStarter.PlayOneShot(SoundDefOf.ExecuteTrade, SoundInfo.OnCamera());
                    }
                }
                GUI.color = Color.white; Text.Font = GameFont.Small;
            }
        }

        // ---------- AUX ----------
        private void RefreshCache(Map map, RamazonSettings st)
        {
            cachedBuyItems = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d =>
                    d.category == ThingCategory.Item &&
                    d.tradeability != Tradeability.None &&
                    !d.IsCorpse &&
                    !typeof(Pawn).IsAssignableFrom(d.thingClass) &&
                    d.BaseMarketValue > 0.0f)
                .ToList();

            if (mode == ShopMode.Sell)
            {
                sellableCounts = ComputeSellableCounts(map);
                cachedSellItems = sellableCounts?.Keys.ToList() ?? new List<ThingDef>();
            }
            else cachedSellItems = new List<ThingDef>();
        }

        private string FormatNumber(long number)
        {
            if (number >= 1_000_000) return $"{number / 1_000_000.0:0.#}M";
            if (number >= 1_000)     return $"{number / 1_000.0:0.#}K";
            return number.ToString("N0");
        }

        private Color GetCategoryColor(ThingDef def)
        {
            if (IsMaterial(def)) return new Color(0.8f, 0.6f, 0.4f);
            if (IsMedicine(def)) return new Color(0.4f, 0.8f, 0.4f);
            if (IsComponent(def)) return new Color(0.6f, 0.6f, 0.8f);
            if (IsBionic(def)) return new Color(0.8f, 0.4f, 0.8f);
            if (IsChip(def))     return new Color(0.4f, 0.8f, 0.8f);
            return new Color(0.7f, 0.7f, 0.7f);
        }
        private string GetCategoryTag(ThingDef def)
        {
            if (IsMaterial(def)) return "MAT";
            if (IsMedicine(def)) return "MED";
            if (IsComponent(def)) return "COMP";
            if (IsBionic(def))    return "BIO";
            if (IsChip(def))      return "CHIP";
            return "OTHER";
        }

        private List<ThingDef> QueryItems_Buy(RamazonSettings st)
        {
            var all = cachedBuyItems?.ToList() ?? new List<ThingDef>();
            if (!st.showStuffables) all = all.Where(d => !d.MadeFromStuff).ToList();

            all = all.Where(d =>
            {
                if (IsMaterial(d)) return fMaterials;
                if (IsMedicine(d)) return fMedicine;
                if (IsComponent(d)) return fComponents;
                if (IsBionic(d))    return fBionics;
                if (IsChip(d))      return fChips;
                return fOther;
            }).ToList();

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim().ToLowerInvariant();
                all = all.Where(d =>
                    (d.label ?? d.defName).ToLowerInvariant().Contains(s) ||
                    d.defName.ToLowerInvariant().Contains(s) ||
                    d.description?.ToLowerInvariant().Contains(s) == true).ToList();
            }

            return sortIdx switch
            {
                0 => all.OrderBy(d => d.label ?? d.defName).ToList(),
                1 => all.OrderByDescending(d => d.BaseMarketValue).ToList(),
                2 => all.OrderBy(d => GetCategoryTag(d)).ThenBy(d => d.label ?? d.defName).ToList(),
                _ => all
            };
        }

        private List<ThingDef> QueryItems_Sell(RamazonSettings st)
        {
            var defs = cachedSellItems?.ToList() ?? new List<ThingDef>();

            defs = defs.Where(d =>
            {
                if (IsMaterial(d)) return fMaterials;
                if (IsMedicine(d)) return fMedicine;
                if (IsComponent(d)) return fComponents;
                if (IsBionic(d))    return fBionics;
                if (IsChip(d))      return fChips;
                return fOther;
            }).ToList();

            if (!st.showStuffables) defs = defs.Where(d => !d.MadeFromStuff).ToList();

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim().ToLowerInvariant();
                defs = defs.Where(d =>
                    (d.label ?? d.defName).ToLowerInvariant().Contains(s) ||
                    d.defName.ToLowerInvariant().Contains(s)).ToList();
            }

            return sortIdx switch
            {
                0 => defs.OrderBy(d => d.label ?? d.defName).ToList(),
                1 => defs.OrderByDescending(d => d.BaseMarketValue).ToList(),
                2 => defs.OrderBy(d => GetCategoryTag(d)).ThenBy(d => d.label ?? d.defName).ToList(),
                _ => defs
            };
        }

        // heurísticas
        private static bool IsMaterial(ThingDef d) =>
            d.thingCategories?.Any(tc => tc.defName.Contains("Resources") || tc.defName.Contains("Metal") || tc.defName.Contains("Stone") || tc.defName.Contains("Raw")) == true ||
            d == ThingDefOf.Steel || d.defName.Contains("Plasteel") || d.defName.Contains("Gold") || d.defName.Contains("Silver") || d.defName.Contains("Uranium") || d.defName.Contains("Wood") || d.defName.Contains("Cloth");

        private static bool IsMedicine(ThingDef d) =>
            d == ThingDefOf.MedicineIndustrial || d.defName.Contains("Medicine") || d.defName.Contains("Neutroamine") || d.defName.Contains("Herbal") || d.defName.Contains("Glitterworld") || d.IsDrug;

        private static bool IsComponent(ThingDef d) =>
            d == ThingDefOf.ComponentIndustrial || d == ThingDefOf.ComponentSpacer || d.defName.Contains("Component") || d.defName.Contains("Gear") || d.defName.Contains("Mechanism");

        private static bool IsBionic(ThingDef d) =>
            d.defName.Contains("Bionic") || d.defName.Contains("Archotech") || d.defName.Contains("LoveEnhancer") || d.defName.Contains("Prosth") || d.defName.Contains("Implant");

        private static bool IsChip(ThingDef d) =>
            d.defName.Contains("Chip") || d.defName.Contains("PersonaCore") || d.defName.Contains("Techprof") || d.defName.Contains("AI") || d.defName.Contains("Mech");

        // ---------- LÓGICA DE COMPRA/VENDA ----------
        private bool IsBuyable(ThingDef def, RamazonSettings st, out string reason)
        {
            reason = null;
            if (def == null) { reason = "Invalid item."; return false; }
            if (typeof(Pawn).IsAssignableFrom(def.thingClass) || def.IsCorpse) { reason = "Cannot buy pawns/corpses."; return false; }
            if (def.BaseMarketValue <= 0f) { reason = "Item has zero market value."; return false; }
            if (def.MadeFromStuff && !st.showStuffables) { reason = "Stuffable item disabled (enable in settings)."; return false; }
            return true;
        }

        private bool AllCartItemsBuyable(RamazonSettings st, out string reason)
        {
            foreach (var def in cart.Keys)
                if (!IsBuyable(def, st, out reason)) return false;
            reason = null;
            return true;
        }

        private bool ConsumeSilver(Map map, int need)
        {
            int remaining = need;
            var stacks = map.listerThings.ThingsOfDef(ThingDefOf.Silver).OrderByDescending(t => t.stackCount).ToList();
            foreach (var t in stacks)
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, t.stackCount);
                t.SplitOff(take).Destroy(DestroyMode.Vanish);
                remaining -= take;
            }
            return remaining <= 0;
        }

        private IntVec3 DoSpawnCart(Map map, RamazonSettings st)
        {
            var spot = DropCellFinder.TradeDropSpot(map);
            var things = new List<Thing>();

            foreach (var kv in cart)
            {
                var def = kv.Key;
                int qty = kv.Value;
                ThingDef stuff = null;

                if (def.MadeFromStuff && st.showStuffables)
                {
                    var wanted = DefDatabase<ThingDef>.GetNamedSilentFail(st.defaultStuffDefName);
                    if (wanted != null && def.stuffCategories != null &&
                        def.stuffCategories.Any(cat => wanted.stuffProps?.categories?.Contains(cat) ?? false))
                        stuff = wanted;
                    else
                    {
                        var steel = ThingDefOf.Steel;
                        if (steel != null && def.stuffCategories != null &&
                            def.stuffCategories.Any(cat => steel.stuffProps?.categories?.Contains(cat) ?? false))
                            stuff = steel;
                    }
                }

                int perStack = Math.Max(1, def.stackLimit);
                int left = qty;
                while (left > 0)
                {
                    int make = Math.Min(perStack, left);
                    var thing = ThingMaker.MakeThing(def, stuff);
                    thing.stackCount = make;
                    things.Add(thing);
                    left -= make;
                }
            }

            DropPodUtility.DropThingsNear(spot, map, things, 110, false, false, true);
            return spot;
        }

        private Dictionary<ThingDef, int> ComputeSellableCounts(Map map)
        {
            if (map?.listerThings == null) return new Dictionary<ThingDef, int>();
            var things = map.listerThings.AllThings
                .Where(t => t?.def != null &&
                            t.def.category == ThingCategory.Item &&
                            t.def.tradeability != Tradeability.None &&
                            !t.def.IsCorpse &&
                            !(t is Pawn) &&
                            t.def.BaseMarketValue > 0f &&
                            t.Spawned &&
                            !t.Position.Fogged(map) &&
                            t.def != ThingDefOf.Silver)
                .ToList();

            var dict = new Dictionary<ThingDef, int>();
            foreach (var t in things)
            {
                if (t?.def == null) continue;
                if (!dict.TryGetValue(t.def, out var c)) c = 0;
                dict[t.def] = c + t.stackCount;
            }
            Log.Message($"[Ramazon] Found {dict.Count} sellable item types with {dict.Values.Sum()} total items");
            return dict;
        }

        private bool CartWithinStock()
        {
            foreach (var kv in cart)
            {
                var def = kv.Key;
                int qty = kv.Value;
                int have = sellableCounts.TryGetValue(def, out var h) ? h : 0;
                if (qty > have) return false;
            }
            return true;
        }

        private void RemoveSoldItemsFromMap(Map map)
        {
            foreach (var kv in cart)
            {
                var def = kv.Key;
                int need = kv.Value;
                if (need <= 0) continue;

                var stacks = map.listerThings.ThingsOfDef(def)
                    .Where(t => t.Spawned && !t.Position.Fogged(map))
                    .OrderByDescending(t => t.stackCount)
                    .ToList();

                foreach (var t in stacks)
                {
                    if (need <= 0) break;
                    int take = Math.Min(need, t.stackCount);
                    t.SplitOff(take).Destroy(DestroyMode.Vanish);
                    need -= take;
                }
            }
        }

        private IntVec3 GiveSilver(Map map, int amount)
        {
            var spot = DropCellFinder.TradeDropSpot(map);
            if (amount <= 0) return spot;

            var things = new List<Thing>();
            int perStack = Math.Max(1, ThingDefOf.Silver.stackLimit);
            int left = amount;
            while (left > 0)
            {
                int make = Math.Min(perStack, left);
                var thing = ThingMaker.MakeThing(ThingDefOf.Silver);
                thing.stackCount = make;
                things.Add(thing);
                left -= make;
            }

            DropPodUtility.DropThingsNear(spot, map, things, 110, false, false, true);
            return spot;
        }

        // ------- Grid helper -------
        private class Listing_Grid
        {
            private Rect _rect;
            private float _cellW, _cellH, _hGap, _vGap;
            private int _cols;
            private float _yCursor;
            private int _colCursor;
            private Rect _viewRect;

            public void Begin(Rect rect, ref Vector2 scrollPosition, float cellW, float cellH, float hGap, float vGap, int columns)
            {
                _rect = rect;
                _cellW = cellW; _cellH = cellH; _hGap = hGap; _vGap = vGap; _cols = Math.Max(1, columns);
                float estRows = 50f;
                float estH = estRows * (cellH + vGap);
                _viewRect = new Rect(rect.x, rect.y, rect.width - 16f, Mathf.Max(rect.height, estH));
                Widgets.BeginScrollView(_rect, ref scrollPosition, _viewRect);
                _yCursor = _viewRect.y + 4f;
                _colCursor = 0;
            }

            public Rect NextCell()
            {
                float x = _viewRect.x + (_colCursor * (_cellW + _hGap));
                var r = new Rect(x, _yCursor, _cellW, _cellH);
                _colCursor++;
                if (_colCursor >= _cols) { _colCursor = 0; _yCursor += _cellH + _vGap; }
                return r;
            }

            public void End() => Widgets.EndScrollView();
        }
    }
}