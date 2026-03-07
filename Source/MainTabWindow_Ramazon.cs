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

            var infoRect = list.GetRect(80f);
            Widgets.DrawMenuSection(infoRect);
            var infoInner = infoRect.ContractedBy(4f);
            if (mode == ShopMode.Buy)
                Widgets.Label(new Rect(infoInner.x, infoInner.y, infoInner.width, 18f), $"Price: {st.priceMultiplier:0.00}× market value");
            else
            {
                Widgets.Label(new Rect(infoInner.x, infoInner.y, infoInner.width, 18f), $"Fee: {st.sellTaxPercent:0}%");
                Widgets.Label(new Rect(infoInner.x, infoInner.y + 18f, infoInner.width, 18f), $"You receive: {(100f - st.sellTaxPercent):0}%");
            }

            GUI.color = Color.yellow;
            Widgets.Label(new Rect(infoInner.x, infoInner.yMax - 18f, infoInner.width, 18f), $"Silver: {FormatNumber(silver)}");
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

            var filtered = (mode == ShopMode.Buy) ? QueryItems_Buy(st) : QueryItems_Sell(st);

            int totalPages = Math.Max(1, Mathf.CeilToInt(filtered.Count / (float)PerPage));
            page = Mathf.Clamp(page, 0, totalPages - 1);

            var headerLeft = new Rect(top.x, top.y, top.width - 200f, 32f);
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
            int columns = Mathf.Max(1, (int)(body.width / 210f));
            grid.Begin(body, ref scroll, cellW: 202f, cellH: 125f, hGap: 8f, vGap: 8f, columns: columns);

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
            Widgets.InfoCardButton(cell.xMax - 50f, cell.y + 4f, def);

            var categoryRect = new Rect(iconRect.xMax + 8, cell.y + 30, 80, 16);
            GUI.color = GetCategoryColor(def);
            Text.Font = GameFont.Tiny; Widgets.Label(categoryRect, GetCategoryTag(def));
            GUI.color = Color.white; Text.Font = GameFont.Small;

            var priceRect = new Rect(iconRect.xMax + 8, cell.y + 48, cell.width - 76, 20);
            var cost = Math.Ceiling(def.BaseMarketValue * st.priceMultiplier);
            Widgets.Label(priceRect, $"MV: {def.BaseMarketValue:0.##} -> {FormatNumber((long)cost)}");

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
            Widgets.InfoCardButton(cell.xMax - 26f, cell.y + 4f, def);

            float unitMV = Math.Max(0f, def.BaseMarketValue);
            float receivePct = (100f - st.sellTaxPercent) / 100f;

            var stockRect = new Rect(iconRect.xMax + 8, cell.y + 28, cell.width - 70, 16);
            Text.Font = GameFont.Tiny; Widgets.Label(stockRect, $"Stock: {FormatNumber(have)}");

            var priceRect = new Rect(iconRect.xMax + 8, cell.y + 44, cell.width - 70, 16);
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
            var titleRect = new Rect(headerRect.x + 8, headerRect.y + 6, headerRect.width - 60, 20);
            var cartTitle = (mode == ShopMode.Buy) ? "Cart" : "Sell Cart";
            Widgets.Label(titleRect, $"{cartTitle} ({cart.Count})");

            var clearRect = new Rect(headerRect.xMax - 52, headerRect.y + 4, 44, 24);
            if (Widgets.ButtonText(clearRect, "Clear") && cart.Count > 0)
            {
                cart.Clear();
                cartQtyBuf.Clear();
                SoundStarter.PlayOneShot(SoundDefOf.CancelMode, SoundInfo.OnCamera());
            }
            Text.Font = GameFont.Small;

            var listRect = new Rect(inner.x, inner.y + 36f, inner.width, inner.height - 170f);
            var contentH = cart.Count * 68f + 8f;
            var viewRect = new Rect(listRect.x, listRect.y, listRect.width - 16f, Math.Max(contentH, listRect.height));

            Widgets.BeginScrollView(listRect, ref cartScroll, viewRect);
            float y = viewRect.y + 4f;
            double subtotalMV = 0f;
            int itemIndex = 0;

            foreach (var kv in cart.ToList())
            {
                var def = kv.Key;
                int qty = kv.Value;
                float unitMV = Math.Max(0f, def.BaseMarketValue);
                subtotalMV += unitMV * qty;

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
            Widgets.EndScrollView();

            var summaryRect = new Rect(inner.x, inner.yMax - 130f, inner.width, 130f);
            GUI.color = CartBackgroundColor; Widgets.DrawBoxSolid(summaryRect, GUI.color); GUI.color = Color.white; Widgets.DrawBox(summaryRect);
            var summaryInner = summaryRect.ContractedBy(8f);

            if (mode == ShopMode.Buy)
            {
                double totalSilver = Math.Ceiling(subtotalMV * st.priceMultiplier);
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y, summaryInner.width, 20), $"Subtotal MV: {FormatNumber((long)subtotalMV)}");
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 22, summaryInner.width, 20), $"Multiplier: {st.priceMultiplier:0.00}x");

                var totalRect = new Rect(summaryInner.x, summaryInner.y + 44, summaryInner.width - 170, 24);
                Text.Font = GameFont.Medium;
                GUI.color = totalSilver <= silver ? Color.green : Color.red;
                Widgets.Label(totalRect, $"Total: {FormatNumber((long)totalSilver)}");
                GUI.color = Color.white; Text.Font = GameFont.Small;

                var affordRect = new Rect(summaryInner.x, summaryInner.y + 70, summaryInner.width - 170, 18);
                if (totalSilver <= silver) { GUI.color = Color.green; Widgets.Label(affordRect, "You can afford this purchase"); }
                else { GUI.color = Color.red; var shortage = totalSilver - silver; Widgets.Label(affordRect, $"Need {FormatNumber((long)shortage)} more silver"); }
                GUI.color = Color.white;

                string reason2 = null;
                bool canCheckout = cart.Count > 0 && totalSilver <= silver && AllCartItemsBuyable(st, out reason2);
                var buyBtnRect = new Rect(summaryInner.xMax - 160, summaryInner.y + 45, 152, 40);
                GUI.color = canCheckout ? BuyModeColor : Color.gray; Text.Font = GameFont.Medium;

                if (Widgets.ButtonText(buyBtnRect, "BUY NOW"))
                {
                    if (!canCheckout) { Messages.Message(reason2 ?? "Cannot complete purchase.", MessageTypeDefOf.RejectInput, false); SoundStarter.PlayOneShot(SoundDefOf.ClickReject, SoundInfo.OnCamera()); }
                    else if (ConsumeSilver(map, (int)totalSilver))
                    {
                        DoSpawnCart(map, st);
                        cart.Clear(); cartQtyBuf.Clear();
                        Messages.Message($"Purchase completed! Paid {FormatNumber((long)totalSilver)} silver.", MessageTypeDefOf.PositiveEvent, false);
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
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y, summaryInner.width, 20), $"Subtotal MV: {FormatNumber((long)subtotalMV)}");
                Widgets.Label(new Rect(summaryInner.x, summaryInner.y + 22, summaryInner.width, 20), $"Fee: {st.sellTaxPercent:0}% • You get: {(receivePct * 100f):0}%");

                var payoutRect = new Rect(summaryInner.x, summaryInner.y + 44, summaryInner.width - 170, 24);
                Text.Font = GameFont.Medium; GUI.color = Color.yellow;
                Widgets.Label(payoutRect, $"Payout: {FormatNumber((long)totalGain)}");
                GUI.color = Color.white; Text.Font = GameFont.Small;

                var stockRect = new Rect(summaryInner.x, summaryInner.y + 70, summaryInner.width - 170, 18);
                bool stockOk = CartWithinStock();
                if (stockOk) { GUI.color = Color.green; Widgets.Label(stockRect, "All items available in stock"); }
                else { GUI.color = Color.red; Widgets.Label(stockRect, "Cart exceeds available stock"); }
                GUI.color = Color.white;

                bool canSell = cart.Count > 0 && stockOk;
                var sellBtnRect = new Rect(summaryInner.xMax - 160, summaryInner.y + 45, 152, 40);
                GUI.color = canSell ? SellModeColor : Color.gray; Text.Font = GameFont.Medium;

                if (Widgets.ButtonText(sellBtnRect, "SELL NOW"))
                {
                    if (!canSell) { Messages.Message("Cannot sell - check stock availability.", MessageTypeDefOf.RejectInput, false); SoundStarter.PlayOneShot(SoundDefOf.ClickReject, SoundInfo.OnCamera()); }
                    else
                    {
                        RemoveSoldItemsFromMap(map);
                        GiveSilver(map, (int)totalGain);
                        cart.Clear(); cartQtyBuf.Clear();
                        sellableCounts = ComputeSellableCounts(map);
                        Messages.Message($"Sale completed! Received {FormatNumber((long)totalGain)} silver.", MessageTypeDefOf.PositiveEvent, false);
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

        private void DoSpawnCart(Map map, RamazonSettings st)
        {
            var spot = DropCellFinder.TradeDropSpot(map);
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
                    GenPlace.TryPlaceThing(thing, spot, map, ThingPlaceMode.Near);
                    left -= make;
                }
            }
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

        private void GiveSilver(Map map, int amount)
        {
            if (amount <= 0) return;
            var spot = DropCellFinder.TradeDropSpot(map);
            int perStack = Math.Max(1, ThingDefOf.Silver.stackLimit);

            int left = amount;
            while (left > 0)
            {
                int make = Math.Min(perStack, left);
                var thing = ThingMaker.MakeThing(ThingDefOf.Silver);
                thing.stackCount = make;
                GenPlace.TryPlaceThing(thing, spot, map, ThingPlaceMode.Near);
                left -= make;
            }
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