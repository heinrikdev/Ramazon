using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Ramazon
{
    /// <summary>
    /// O portal do Ramazon. Queima OURO para manter a ligacao aberta:
    /// abastecido fica aceso (PortalOn) e operante; sem ouro apaga (PortalOff)
    /// e a rede nao entrega nada.
    /// As entregas materializam na celula de interacao (a frente do portal).
    /// </summary>
    public class Building_RamazonPortal : Building
    {
        public const string PortalDefName = "Ramazon_Portal";

        private static Graphic onGraphic;
        private CompRefuelable refuelableCached;

        private CompRefuelable Refuelable
        {
            get
            {
                if (refuelableCached == null) refuelableCached = GetComp<CompRefuelable>();
                return refuelableCached;
            }
        }

        /// <summary>Tem ouro queimando? (aceso e operante)</summary>
        public bool IsActive
        {
            get
            {
                var r = Refuelable;
                return r != null && r.HasFuel;
            }
        }

        /// <summary>Celula onde as entregas materializam: a frente do portal.</summary>
        public IntVec3 DeliveryCell
        {
            get
            {
                var c = InteractionCell;
                if (c.IsValid && Map != null && c.InBounds(Map)) return c;
                return Position;
            }
        }

        private Graphic OnGraphic
        {
            get
            {
                if (onGraphic == null && def != null && def.graphicData != null)
                {
                    onGraphic = GraphicDatabase.Get<Graphic_Single>(
                        "Ramazon/PortalOn",
                        ShaderDatabase.Transparent,
                        def.graphicData.drawSize,
                        Color.white);
                }
                return onGraphic;
            }
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (IsActive)
            {
                var g = OnGraphic;
                if (g != null)
                {
                    g.Draw(drawLoc, Rot4.North, this);
                    return;
                }
            }
            base.DrawAt(drawLoc, flip);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Ramazon",
                defaultDesc = "Abrir o catalogo do Ramazon. As entregas saem por este portal.",
                icon = ContentFinder<Texture2D>.Get("UI/Icons/Ramazon", false),
                action = delegate
                {
                    var existing = Find.WindowStack.WindowOfType<MainTabWindow_Ramazon>();
                    if (existing != null) existing.Close();
                    else Find.WindowStack.Add(new MainTabWindow_Ramazon());
                }
            };
        }

        public override string GetInspectString()
        {
            var baseStr = base.GetInspectString();
            var line = IsActive
                ? "Rede Ramazon: online (queimando ouro)"
                : "Rede Ramazon: OFFLINE - abasteca com ouro";
            return string.IsNullOrEmpty(baseStr) ? line : baseStr + "\n" + line;
        }
    }
}
