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

## Usage

1. **Import** — Select your MakePlace `.list` file and click **Load & Resolve Items**
2. **Exclude** — Uncheck any items you plan to source elsewhere
3. **Fetch Prices** — Queries Universalis for current cross-world listings
4. **Shopping List** — Review the plan grouped by datacenter and world
5. **Start Shopping** — The plugin teleports to each world, opens the marketboard, and purchases items automatically

### Notes

- High-value items (above the auto-approve threshold) will prompt for confirmation before purchasing
- The plugin pauses automatically if your inventory is nearly full and shows a banner to deposit items
- Items that could not be fully purchased appear in a **Missed Items** summary at the end of the run with a copy-to-clipboard button

## Settings

| Setting | Description |
|---|---|
| Auto-approve below | Items at or below this price are purchased without a confirmation prompt |
| Extra warning above | Items above this price show an additional warning |
| Auto-skip high value | Automatically skip items over the auto-approve threshold |
| Prefer NQ | Prefer normal quality listings over high quality |
| Only search current DC / world | Restrict price fetching and shopping to your datacenter or world |
| Navigation delay | Milliseconds to wait between navigation steps |
| Enabled Datacenters | Uncheck datacenters to exclude them from price fetching and shopping |
| Auto-pause when inventory full | Pauses before a world if your inventory won't have enough free slots |
