using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace Openings
{
    public class App : IExternalApplication
    {
        private UIControlledApplication _uiApp;

        public Result OnStartup(UIControlledApplication application)
        {
            UpdateChecker.CheckInBackground();
            // Tracks openings changed or deleted by hand, and the MEP elements they belong to,
            // from Revit start-up on — not only once the palette has been opened.
            OpeningUpdater.Register(application.ActiveAddInId);

            string tab = Brand.RibbonTab;
            CreateTabIfNotExists(application, tab);

            RibbonPanel panel = GetOrCreatePanel(application, tab, Brand.RibbonPanel);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Shared F1 (contextual) help target for every button.
            ContextualHelp help = new ContextualHelp(ContextualHelpType.Url, Brand.HelpUrl);

            PushButtonData openingsBtn = new PushButtonData("VixeldorfOpeningsBtn", "Openings", assemblyPath, "Openings.OpeningsCommand")
            {
                ToolTip = "Place, update and track openings where ducts, pipes, cable trays and shafts cross walls and floors.",
                LongDescription = "Opens the Openings palette: walls and floors from the current model or links, openings as "
                                + "Generic Models or face-based families, sketch editing, Lock, and openings that follow "
                                + "moved MEP elements and go away with deleted ones.",
                LargeImage = LoadImage("openings_32.png"),
                Image = LoadImage("openings_16.png")
            };
            openingsBtn.SetContextualHelp(help);
            panel.AddItem(openingsBtn);

            // ── About / Help button ───────────────────────────────────────────
            // Every add-in of this brand shares one "About" panel holding a single help button, so
            // only the first one loaded gets to add it. Two independent checks, because neither alone
            // covers every case:
            //   • an AppDomain data slot — all add-ins in a Revit process share one AppDomain, and this
            //     catches siblings built from the same skill regardless of load order;
            //   • a scan of the live ribbon via AdWindows — RibbonPanel.GetItems() only sees items the
            //     CURRENT add-in added, so this is the only way to notice a button placed by an older
            //     add-in of the same brand.
            RibbonPanel aboutPanel = GetOrCreatePanel(application, tab, Brand.AboutPanel);
            bool alreadyAdded = AppDomain.CurrentDomain.GetData(Brand.AboutFlagKey) != null
                                || AboutButtonExistsOnRibbon(tab, Brand.AboutPanel);
            if (!alreadyAdded)
            {
                AppDomain.CurrentDomain.SetData(Brand.AboutFlagKey, true);
                PushButtonData webButtonData = new PushButtonData(
                    Brand.AboutButtonId, Brand.AboutButtonText,
                    assemblyPath, "Openings.OpenWebPage")
                {
                    ToolTip = Brand.AboutButtonTooltip,
                    LargeImage = LoadImage("web_icon.png")
                };
                webButtonData.SetContextualHelp(help);
                aboutPanel.AddItem(webButtonData);
            }

            // The "About" panel must stay at the very end of the tab even when other
            // Vixeldorf add-ins add their own panels later. There is no supported API to
            // reorder ribbon panels; ApplicationInitialized can fire before the ribbon is
            // fully built (and never fires when the add-in loads after Revit has started),
            // so we reorder on the first Idling tick — raised once, when the ribbon exists.
            if (Brand.PinAboutPanelToEnd)
            {
                _uiApp = application;
                application.Idling += OnFirstIdling;
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private void OnFirstIdling(object sender, IdlingEventArgs e)
        {
            _uiApp.Idling -= OnFirstIdling;   // one-shot: only the first idle
            MoveAboutPanelToEnd();
        }

        private static bool AboutButtonExistsOnRibbon(string tabTitle, string panelTitle)
        {
            try
            {
                Autodesk.Windows.RibbonControl ribbon = Autodesk.Windows.ComponentManager.Ribbon;
                if (ribbon == null) return false;

                foreach (Autodesk.Windows.RibbonTab ribbonTab in ribbon.Tabs)
                {
                    if (!string.Equals(ribbonTab.Title, tabTitle, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (Autodesk.Windows.RibbonPanel ribbonPanel in ribbonTab.Panels)
                    {
                        if (ribbonPanel.Source == null ||
                            !string.Equals(ribbonPanel.Source.Title, panelTitle, StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (Autodesk.Windows.RibbonItem item in ribbonPanel.Source.Items)
                            if (ItemTextMatchesAboutButton(item)) return true;
                    }
                }
            }
            catch { /* AdWindows unavailable — fall through and add the button */ }
            return false;
        }

        private static bool ItemTextMatchesAboutButton(Autodesk.Windows.RibbonItem item)
        {
            if (item == null) return false;
            string text = item.Text ?? string.Empty;
            if (text.IndexOf(Brand.AboutMatchText, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            Autodesk.Windows.RibbonRowPanel rowPanel = item as Autodesk.Windows.RibbonRowPanel;
            if (rowPanel != null)
                foreach (Autodesk.Windows.RibbonItem child in rowPanel.Items)
                    if (ItemTextMatchesAboutButton(child)) return true;

            return false;
        }

        // Uses the (unofficial but long-stable) Autodesk.Windows ribbon model to move the
        // shared "About" panel to the end of the tab. Wrapped so it can never break startup.
        private static void MoveAboutPanelToEnd()
        {
            try
            {
                Autodesk.Windows.RibbonControl ribbon = Autodesk.Windows.ComponentManager.Ribbon;
                if (ribbon == null)
                    return;

                foreach (Autodesk.Windows.RibbonTab tab in ribbon.Tabs)
                {
                    if (tab.Title != Brand.RibbonTab && tab.Id != Brand.RibbonTab)
                        continue;

                    Autodesk.Windows.RibbonPanel about = null;
                    foreach (Autodesk.Windows.RibbonPanel p in tab.Panels)
                    {
                        if (p.Source != null && p.Source.Title != null &&
                            p.Source.Title.Trim().Equals(Brand.AboutPanel, StringComparison.OrdinalIgnoreCase))
                        {
                            about = p;
                            break;
                        }
                    }

                    if (about != null && tab.Panels[tab.Panels.Count - 1] != about)
                    {
                        tab.Panels.Remove(about);
                        tab.Panels.Add(about);
                    }
                    break;
                }
            }
            catch
            {
                // Never let a ribbon-cosmetics tweak interfere with loading.
            }
        }

        private void CreateTabIfNotExists(UIControlledApplication app, string tabName)
        {
            try { app.CreateRibbonTab(tabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        }

        private RibbonPanel GetOrCreatePanel(UIControlledApplication app, string tabName, string panelName)
        {
            foreach (RibbonPanel panel in app.GetRibbonPanels(tabName))
                if (panel.Name == panelName) return panel;
            return app.CreateRibbonPanel(tabName, panelName);
        }

        private BitmapImage LoadImage(string resourceName)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                System.IO.Stream stream = asm.GetManifestResourceStream($"Openings.Resources.{resourceName}");
                if (stream == null)
                    foreach (string n in asm.GetManifestResourceNames())
                        if (n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
                        { stream = asm.GetManifestResourceStream(n); break; }

                if (stream == null) return null;

                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
    }
}
