package {
import flash.display.*;
import flash.external.ExternalInterface;
import flash.utils.Dictionary;
// The game's own objects as skua.swf's getGameObject reads them: JSON.stringify, then back over the Bridge.
// Shaped like the live game (#49): inventory items with CharItemIDs past 2^28, a weak-key Dictionary of
// players keyed by name that the game enumerates and edits (read as the Engine does: the names from a
// Vector.<String>, then each player by key), a class instance with getters, and big payloads.
public class Reads extends Sprite {
    private var world:Object = {};

    public function Reads() {
        world.items = items(17, 0);
        world.bank = items(1000, 100);
        world.uoTree = players();
        world.areaUsers = new Vector.<String>();
        for (var name:String in world.uoTree) world.areaUsers.push(name);
        world.odd = {negZero: -0, half: 0.5, big: 4294967296, huge: 1e21, tiny: 1.5e-7, min: -268435457, nan: NaN,
            text: "a & <b> \"q\" ' ’ é 🐉 \\ / \t\n"};
        world.bankinfo = new Bank(world.bank);
        ExternalInterface.addCallback("getGameObject", getGameObject);
        ExternalInterface.addCallback("getGameObjectKey", getGameObjectKey);
    }

    private function getGameObjectKey(path:String, key:String):String {
        return JSON.stringify(world[path][key]);
    }

    private function getGameObject(path:String):String {
        var obj:* = world;
        for each (var part:String in path.split(".")) obj = obj[part];
        return JSON.stringify(obj);
    }

    private static function items(n:int, from:int):Array {
        var a:Array = [];
        for (var i:int = from; i < from + n; i++)
            a.push({ItemID: i + 1, CharItemID: 1500000000 + i, sName: "Item " + i, sDesc: "Tom & Jerry's <sword> ’",
                iQty: i % 7 + 1, iStk: 99, bEquip: i == from, sType: i == from ? "Class" : "Sword", EnhLvl: 0, bTemp: false,
                iHrs: 12.25, tPurchase: 1790499068112 + i});
        return a;
    }

    // world.uoTree: new Dictionary(true) with string keys; players leave and join while the game enumerates it.
    private static function players():Dictionary {
        var d:Dictionary = new Dictionary(true);
        var names:Array = ["theknightofblood", "ladyapothecary", "maada", "yzobelle", "bpgeo", "bal_"];
        for (var i:int = 0; i < names.length; i++)
            d[names[i]] = {uoName: names[i], entID: 4760 + i, strFrame: "Enter", strPad: "Spawn", intState: 1,
                intLevel: 100, intHP: 3990, intHPMax: 3990, intMP: 100, tx: 512.5, ty: 400, afk: false,
                sta: {$STR: 43, $tha: 0.1}, auras: [{nam: "Vermilion Pact", ts: 1790499068112}]};
        for (var k:String in d) break;
        delete d["bpgeo"];
        for (k in d) {}
        delete d["theknightofblood"];
        d["asylim"] = {uoName: "asylim", entID: 4186, strFrame: "Enter", strPad: "Spawn", intState: 1, intLevel: 100,
            intHP: 2345, intHPMax: 2345, intMP: 100, tx: 166, ty: 282, afk: true};
        return d;
    }
}
}

// The game's BankController: world.bankinfo.items is a getter.
class Bank {
    private var arr:Array;
    public var Count:int = 0;
    public function Bank(items:Array) { arr = items; Count = items.length; }
    public function get items():Array { return arr; }
}
