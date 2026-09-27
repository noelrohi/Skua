package {
import flash.display.*;
import flash.events.*;
import flash.external.ExternalInterface;
import flash.utils.getTimer;
// Numbered events in three streams, interleaved like pext/packet/packetFromServer, at a high rate.
public class Events extends Sprite {
    private var n:int = 0;
    public function Events() { addEventListener(Event.ENTER_FRAME, onFrame); }
    private function onFrame(e:Event):void {
        if (n >= 30000) return;
        for (var i:int = 0; i < 300; i++) {
            var name:String = (n % 3 == 0) ? "pext" : (n % 3 == 1) ? "packetFromServer" : "packet";
            ExternalInterface.call(name, '{"seq":' + n + ',"t":' + getTimer() + ',"pad":"' + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" + '"}');
            n++;
        }
    }
}
}
