package {
import flash.display.Sprite;
import flash.net.SharedObject;

// The game's local storage as AQW client 5.0 keeps it (Game.as: SharedObject.getLocal("AQLite_Data", "/"), its Favorites among it): traces
// how many runs it read back, then counts this one and flushes. tests/game_storage.rs runs it; Storage.swf is it built with the Flex SDK
// that Skua.AS3/compile-as3.sh caches:
//   PLAYERGLOBAL_HOME=<flex>/frameworks/libs/player <flex>/bin/mxmlc -target-player 32.0 -omit-trace-statements=false \
//       -output tests/fixtures/Storage.swf tests/fixtures/Storage.as
public class Storage extends Sprite {
    public function Storage() {
        var data:SharedObject = SharedObject.getLocal("AQLite_Data", "/");
        var runs:int = data.data.runs == undefined ? 0 : int(data.data.runs);
        trace("read " + runs);
        data.data.runs = runs + 1;
        trace("flush " + data.flush());
    }
}
}
