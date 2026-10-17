# Test fixtures

- `home-html.10.11.10.chunk.js`: the compiled home-page template chunk from the official
  `linuxserver/jellyfin:10.11.10` image (`/usr/share/jellyfin/web/home-html.8ce38bc7d6dc073656d4.chunk.js`),
  part of jellyfin-web (GPL-2.0, https://github.com/jellyfin/jellyfin-web). Used to reproduce how
  Custom Tabs 0.2.10.0 injects tab HTML into this file.
- `v1.0.0.0-custom-tab.html`: the tab HTML that Media Vote 1.0.0.0 wrote into Custom Tabs' settings
  (it broke the home page; kept only as a regression fixture).
