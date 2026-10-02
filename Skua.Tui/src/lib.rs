//! skua-tui: a terminal UI for windowless Engines, over the Control Surface (ADR 0002). It reads the Skua Manager's accounts and never
//! writes them.

pub mod actions;
pub mod app;
pub mod chat;
pub mod discovery;
pub mod dto;
pub mod engine;
pub mod poller;
pub mod rpc;
pub mod ui;
