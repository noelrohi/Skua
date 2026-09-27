//! Command line and the headless defaults (#13, #17).

use std::path::PathBuf;
use std::time::Duration;

pub const USAGE: &str = "\
usage: skua-gamehost [options] <skua.swf>

  --show-game                  show the game in a debug window (renders every 33 ms, no render budget)
  --render-interval-ms=N       keep-alive render interval (default 1000; 0 = never render unasked)
  --render-budget-pct=N        render at most N% of wall time (default 10; 0 = off)
  --render-max-interval-ms=N   never stretch the render interval beyond this (default 5000)
  --no-render-thread           run the GPU half of a frame on the main thread
  --pass-budget=N              render passes per submission (default 64)
  --max-in-flight=N            GPU submissions in flight (default 2; 0 = unbounded)
  --layer-flush=N              draw finished blend layers after every N (default 8; 0 = upstream)
  --trim-ticks=N               trim the offscreen texture pool every N ticks (default 30; 0 = off)";

/// How often the host renders when nobody asks for a frame.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RenderPolicy {
    /// `None`: only screenshots render.
    pub interval: Option<Duration>,
    /// After a frame that cost C, wait at least C * 100 / budget_pct (0 = off).
    pub budget_pct: u32,
    /// The budget never stretches the interval beyond this.
    pub max_interval: Duration,
}

impl RenderPolicy {
    /// The wait before the next render, given what the last one cost; `None` if the host doesn't render unasked.
    pub fn gap(&self, last_cost: Duration) -> Option<Duration> {
        let interval = self.interval?;
        if self.budget_pct == 0 {
            return Some(interval);
        }
        let stretched = last_cost * 100 / self.budget_pct;
        Some(interval.max(stretched.min(self.max_interval)))
    }
}

#[derive(Debug, PartialEq)]
pub struct Opts {
    pub swf: PathBuf,
    pub show_game: bool,
    pub render: RenderPolicy,
    pub render_thread: bool,
    pub pass_budget: u32,
    pub max_in_flight: u32,
    pub layer_flush: u32,
    /// 0 = off.
    pub trim_ticks: u32,
}

/// The debug window's frame interval.
pub const SHOW_GAME_INTERVAL: Duration = Duration::from_millis(33);

impl Opts {
    pub fn parse(args: impl IntoIterator<Item = String>) -> Result<Opts, String> {
        let mut swf = None;
        let mut opts = Opts {
            swf: PathBuf::new(),
            show_game: false,
            render: RenderPolicy {
                interval: Some(Duration::from_millis(1000)),
                budget_pct: 10,
                max_interval: Duration::from_millis(5000),
            },
            render_thread: true,
            pass_budget: 64,
            max_in_flight: 2,
            layer_flush: 8,
            trim_ticks: 30,
        };
        for arg in args {
            let (flag, value) = match arg.split_once('=') {
                Some((f, v)) if f.starts_with("--") => (f, Some(v)),
                _ => (arg.as_str(), None),
            };
            let n = || -> Result<u32, String> {
                let v = value.ok_or_else(|| format!("{flag} needs a value"))?;
                v.parse().map_err(|_| format!("{flag}: not a number: {v}"))
            };
            let ms = || n().map(|n| Duration::from_millis(n.into()));
            match flag {
                "--show-game" if value.is_none() => opts.show_game = true,
                "--no-render-thread" if value.is_none() => opts.render_thread = false,
                "--render-interval-ms" => opts.render.interval = Some(ms()?).filter(|d| !d.is_zero()),
                "--render-budget-pct" => opts.render.budget_pct = n()?,
                "--render-max-interval-ms" => opts.render.max_interval = ms()?,
                "--pass-budget" => opts.pass_budget = n()?,
                "--max-in-flight" => opts.max_in_flight = n()?,
                "--layer-flush" => opts.layer_flush = n()?,
                "--trim-ticks" => opts.trim_ticks = n()?,
                f if f.starts_with('-') => return Err(format!("unknown option {arg}")),
                _ if swf.is_some() => return Err(format!("more than one SWF: {arg}")),
                _ => swf = Some(PathBuf::from(arg.clone())),
            }
        }
        opts.swf = swf.ok_or("no SWF given")?;
        if opts.show_game {
            opts.render.interval = Some(SHOW_GAME_INTERVAL);
            opts.render.budget_pct = 0;
        }
        Ok(opts)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(args: &[&str]) -> Result<Opts, String> {
        Opts::parse(args.iter().map(|s| s.to_string()))
    }

    const fn ms(n: u64) -> Duration {
        Duration::from_millis(n)
    }

    #[test]
    fn headless_defaults() {
        let o = parse(&["skua.swf"]).unwrap();
        assert_eq!(o.swf, PathBuf::from("skua.swf"));
        assert!(!o.show_game);
        assert!(o.render_thread);
        assert_eq!(
            o.render,
            RenderPolicy {
                interval: Some(ms(1000)),
                budget_pct: 10,
                max_interval: ms(5000)
            }
        );
        assert_eq!(
            (o.pass_budget, o.max_in_flight, o.layer_flush, o.trim_ticks),
            (64, 2, 8, 30)
        );
    }

    #[test]
    fn flags_override_the_defaults() {
        let o = parse(&[
            "--render-interval-ms=250",
            "--render-budget-pct=0",
            "--render-max-interval-ms=30000",
            "--no-render-thread",
            "--pass-budget=256",
            "--max-in-flight=0",
            "--layer-flush=0",
            "--trim-ticks=0",
            "x.swf",
        ])
        .unwrap();
        assert_eq!(o.swf, PathBuf::from("x.swf"));
        assert!(!o.render_thread);
        assert_eq!(
            o.render,
            RenderPolicy {
                interval: Some(ms(250)),
                budget_pct: 0,
                max_interval: ms(30000)
            }
        );
        assert_eq!(
            (o.pass_budget, o.max_in_flight, o.layer_flush, o.trim_ticks),
            (256, 0, 0, 0)
        );
    }

    #[test]
    fn a_zero_interval_turns_keep_alive_renders_off() {
        assert_eq!(
            parse(&["--render-interval-ms=0", "x.swf"]).unwrap().render.interval,
            None
        );
    }

    #[test]
    fn show_game_renders_every_33_ms_without_a_budget() {
        let o = parse(&["x.swf", "--show-game", "--render-interval-ms=0"]).unwrap();
        assert!(o.show_game);
        assert_eq!(o.render.gap(ms(900)), Some(ms(33)));
    }

    #[test]
    fn rejects_bad_command_lines() {
        assert!(parse(&[]).is_err());
        assert!(parse(&["a.swf", "b.swf"]).is_err());
        assert!(parse(&["--bogus", "a.swf"]).is_err());
        assert!(parse(&["--pass-budget", "a.swf"]).is_err());
        assert!(parse(&["--pass-budget=lots", "a.swf"]).is_err());
        assert!(parse(&["--show-game=1", "a.swf"]).is_err());
    }

    #[test]
    fn budget_stretches_the_interval_after_a_costly_frame() {
        let p = RenderPolicy {
            interval: Some(ms(1000)),
            budget_pct: 10,
            max_interval: ms(5000),
        };
        assert_eq!(p.gap(Duration::ZERO), Some(ms(1000)));
        assert_eq!(p.gap(ms(97)), Some(ms(1000)));
        assert_eq!(p.gap(ms(300)), Some(ms(3000)));
        assert_eq!(p.gap(ms(800)), Some(ms(5000)));
    }

    #[test]
    fn no_budget_means_a_fixed_interval() {
        let p = RenderPolicy {
            interval: Some(ms(250)),
            budget_pct: 0,
            max_interval: ms(5000),
        };
        assert_eq!(p.gap(ms(900)), Some(ms(250)));
    }

    #[test]
    fn the_cap_never_shortens_the_interval() {
        let p = RenderPolicy {
            interval: Some(ms(8000)),
            budget_pct: 10,
            max_interval: ms(5000),
        };
        assert_eq!(p.gap(ms(900)), Some(ms(8000)));
    }

    #[test]
    fn no_interval_means_no_unasked_renders() {
        let p = RenderPolicy {
            interval: None,
            budget_pct: 10,
            max_interval: ms(5000),
        };
        assert_eq!(p.gap(ms(10)), None);
    }
}
