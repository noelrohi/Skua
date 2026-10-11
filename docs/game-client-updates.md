# Checking a new AQW game client

AQW replaces its game client without notice, and a new one can break what Skua reads of the game: the client that replaced `Game4000r1.swf` (client 5.01) with `Game4000r2.swf` (5.1) dropped `InvCat.MISC_SLOTS`, and Misc Space read 0 on every account until someone asked about bag space.

## Noticing one

`https://game.aq.com/game/api/data/gameversion` names the current client, which skua.swf loads:

```json
{"sFile":"Game4000r2.swf","sTitle":"18th Upholder!","sBG":"Generic2.swf","sVersion":"R0048"}
```

Each time the Game Client loads, the Engine records it as a `game.version` event (`{file, version, title}`), and `skua status` names it on the Game line (`client Game4000r2.swf R0048`). The example Hook [`docs/hooks/game.version`](hooks/game.version) says when the file changes.

## Checking it

1. Download the new client and the previous one; the game keeps serving old files at the same path:

   ```sh
   mkdir -p /tmp/aqw && cd /tmp/aqw
   curl -O https://game.aq.com/game/gamefiles/Game4000r2.swf
   curl -O https://game.aq.com/game/gamefiles/Game4000r1.swf
   ```

2. Decompile both with FFDec, which this repo ships in `FFDec/` (Java 17 works; each takes about a minute):

   ```sh
   java -jar <checkout>/FFDec/ffdec.jar -export script r2 Game4000r2.swf
   java -jar <checkout>/FFDec/ffdec.jar -export script r1 Game4000r1.swf
   ```

   The scripts land in `r2/scripts/`, one `.as` file per class, in folders by package.

3. Diff the files Skua depends on:

   ```sh
   for f in InvCat.as Game.as Avatar.as World.as liteAssets/draw/FavStore.as liteAssets/draw/AuraHUD.as; do
       diff -u "r1/scripts/$f" "r2/scripts/$f"
   done
   ```

   | File | What Skua reads |
   |---|---|
   | `InvCat.as` | Which Space an item fills and whether it fits (`poolOf`, `isFullFor`, Misc Space's size); `Skua.AS3/skua/src/skua/api/Inventory.as` |
   | `Game.as` | The packet handlers for `loadInventoryBig`, `addItems` and `getBank`/`loadBank`, and `vParam`, the client's version (`5.1`) |
   | `Avatar.as` | The bank (`initBank`, `addToBank`, `iBankCount`) and `addItem` |
   | `World.as` | The packet senders Skua calls (`sendLoadShopRequest`, `acceptQuest`, `tryQuestComplete`, `toggleBank` and the like) and the inventory-full messages |
   | `liteAssets/draw/FavStore.as` | The player's Favorites |
   | `liteAssets/draw/AuraHUD.as` | The HUD auras |

   Ignore what the decompiler writes differently between runs, such as `int(int(x))` for `int(x)` or renumbered `_locN_` locals. For each real change, find where Skua uses the name (`grep -rn <name> Skua.AS3 Skua.Core Skua.Engine`) and check it still holds; the FakeGameHost (`Skua.FakeGameHost/FakeGame.cs`) simulates some of these rules, and should follow the new client too.
