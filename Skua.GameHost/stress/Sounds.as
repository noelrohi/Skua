package {
import flash.display.*;
import flash.events.*;
import flash.media.*;
// #13: AQW SoundFX pattern. Play a 0.2 s sound every frame, keep the channel until SOUND_COMPLETE.
// On a backend where sounds never complete, `active` grows without bound.
public class Sounds extends Sprite {
    [Embed(source="beep.mp3")] private static const Beep:Class;
    private var snd:Sound = new Beep() as Sound;
    private var active:Vector.<SoundChannel> = new Vector.<SoundChannel>();
    private var frames:int = 0;
    public function Sounds() { trace("start " + snd); addEventListener(Event.ENTER_FRAME, onFrame); }
    private function onFrame(e:Event):void {
        var ch:SoundChannel = snd.play();
        if (ch != null) { active.push(ch); ch.addEventListener(Event.SOUND_COMPLETE, onDone, false, 0, true); }
        if (++frames % 30 == 0) trace("active " + active.length);
    }
    private function onDone(e:Event):void {
        var i:int = active.indexOf(e.target as SoundChannel);
        if (i >= 0) active.splice(i, 1);
    }
}
}
