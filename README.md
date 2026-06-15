# HousingMarketShopper

A Dalamud plugin for FFXIV that reads a MakePlace furnishing list, finds the cheapest cross-world listings via Universalis, and automates the shopping run using Lifestream.

## Requirements

- [Dalamud](https://github.com/goatcorp/Dalamud) (API level 15)
- [Lifestream](https://github.com/NightmareXIV/Lifestream) — required for automated world travel and marketboard navigation

## Installation

This plugin is not on the official Dalamud plugin repository. Add it as a custom/third-party repo:

1. Open **Dalamud Settings** → **Experimental** tab
2. Under **Custom Plugin Repositories**, paste the URL below and click **+**:
   ```
   https://raw.githubusercontent.com/Yodhrin13/HousingMarketShopper/main/pluginmaster.json
   ```
3. Click **Save & Close**
4. Open the **Plugin Installer**, search for **HousingMarketShopper**, and install

Open the plugin with `/hms` (or `/hms config` for settings).

## Features

- **Cross-world price sourcing** — queries Universalis for every enabled datacenter and builds a plan that buys each item at the cheapest world that can supply the full quantity.
- **World consolidation** — items are kept on a world already in the plan when its price is within a configurable tolerance, cutting down on world-hopping.
- **Automated shopping** — teleports world to world via Lifestream, opens the marketboard, and purchases items for you.
- **Manual + persistent item resolution** — unresolved or fuzzy-matched items can be fixed with a searchable picker; corrections are remembered and reapplied on future imports.
- **Saved lists** — save, load, and delete named shopping lists.
- **Resume after a crash** — run progress is saved to disk after every purchase, so a crash or logout doesn't force you to start over.
- **Dry run** — log the full route and intended purchases without travelling or buying anything.
- **Budget tools** — an optional budget cap drops the most expensive items to keep a plan under a gil ceiling, and a gil-on-hand check warns if a plan exceeds your current gil.
- **Per-item world comparison** — open a popup to see every world's price for an item (aggregated for the quantity you need), or jump straight to the item on Universalis.
- **Plan insight** — sort/filter the plan, see how many worlds consolidation saved you, and get a rough travel-time estimate.
- **Inventory awareness** — pauses before a world if you won't have enough free slots, with a one-click teleport to Ul'dah to deposit.

## Usage

1. **Import** — Select your MakePlace `.list` file and click **Load & Resolve Items**.
2. **Adjust** — Uncheck any items you plan to source elsewhere, edit quantities inline, and use **Fix** to correct any unresolved or fuzzy-matched items. Optionally save the list under a name for later.
3. **Fetch Prices** — Queries Universalis for current cross-world listings.
4. **Shopping List** — Review the plan grouped by datacenter and world. Sort/filter it, check per-item world prices, and run a **Dry Run** to preview the route.
5. **Start Shopping** — The plugin teleports to each world, opens the marketboard, and purchases items automatically.

### Notes

- **Resolution colours** — green = exact match, amber = fuzzy match (shows what it matched and the edit distance), red = unresolved. Click **Fix** on any non-exact item to pick the correct one; the choice is saved across imports.
- **High-value items** (above the auto-approve threshold) prompt for confirmation, showing the expected source world and the snapshot listings/retainers you'll buy from.
- **Listing age** — items whose cheapest listing is older than the stale threshold are flagged with a ⏱ in the plan, so you know the price may be out of date.
- **Inventory** — the plugin pauses before a world if your inventory won't have enough free slots and shows a banner to deposit items.
- **Missed items** — anything not fully purchased appears in a **Missed Items** summary at the end of the run, with **Copy** and **Retry** buttons (Retry re-shops only those items, no re-fetch needed).
- **Resume** — if a run is interrupted by a crash or logout, a **Resume Run** banner appears on the Shopping List tab next session.

## Settings

| Setting | Description |
|---|---|
| Auto-approve below | Items at or below this price are purchased without a confirmation prompt |
| Extra warning above | Items above this price show an additional warning |
| Auto-skip high value | Automatically skip items over the auto-approve threshold |
| Max price premium (%) | How far above the Universalis snapshot a live listing may be and still be bought automatically |
| Prefer NQ | Prefer normal quality listings over high quality |
| Only search current DC / world | Restrict price fetching and shopping to your datacenter or world |
| World consolidation tolerance (%) | Keep items on a world already in the plan when within this % of the cheapest (0 = always absolute cheapest) |
| Stale listing warning (hours) | Listings older than this are flagged with a ⏱ in the plan |
| Budget cap (gil) | Drop the most expensive items to keep a plan under this total (0 = no cap) |
| Navigation delay | Milliseconds to wait between navigation steps |
| Enabled Datacenters | Uncheck datacenters to exclude them from price fetching and shopping |
| Auto-pause when inventory full | Pauses before a world if your inventory won't have enough free slots |
