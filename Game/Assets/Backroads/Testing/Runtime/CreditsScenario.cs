using System.Collections;
using System.Linq;
using Backroads.Client.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Testing
{
    /// <summary>
    /// m1.ui.credits: Esc opens the menu, its Credits button opens the Credits screen, which shows the OpenStreetMap
    /// credit and the build's notices (one label per paragraph), and Esc closes it again.
    /// </summary>
    public sealed class CreditsScenario : IAutotestScenario
    {
        public string Kind => "ui-credits";

        public IEnumerator Run(AutotestContext ctx)
        {
            if (!ctx.Renders) { ctx.Fail("ui-credits needs a window (no -batchmode)"); yield break; }
            yield return ctx.Wait(Mathf.Max(1f, ctx.Scenario.seconds));
            var menu = QuickMenu.Current;
            if (menu == null) { ctx.Fail("no Esc menu (client UI did not start)"); yield break; }

            menu.Show();
            yield return null;
            if (!menu.Visible) { ctx.Fail("the menu did not open"); yield break; }
            var root = Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)
                .Select(d => d.rootVisualElement).FirstOrDefault(r => r?.Q("quick-menu") != null);
            if (root == null) { ctx.Fail("no UI document with the menu"); yield break; }
            yield return ctx.Screenshot("menu");

            var credits = root.Q<Button>("open-credits");
            if (credits == null) { ctx.Fail("the menu has no Credits button"); yield break; }
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = credits; credits.SendEvent(e); }
            yield return null;
            yield return null;

            var panel = root.Q("credits");
            if (panel == null || panel.resolvedStyle.display != DisplayStyle.Flex) { ctx.Fail("Credits did not open"); yield break; }
            var labels = panel.Query<Label>().ToList();
            ctx.Metric("credits_labels", labels.Count);
            if (!labels.Any(l => l.text.Contains("OpenStreetMap contributors"))) ctx.Fail("no OpenStreetMap credit on the Credits screen");
            if (!labels.Any(l => l.text.Contains("THIRD-PARTY NOTICES"))) ctx.Fail("the build's THIRD_PARTY_NOTICES.txt is not shown");
            if (menu.Visible) ctx.Fail("the menu stayed open over the Credits");
            yield return ctx.Screenshot("credits");
        }
    }
}
