// Times mail-parser and mail-builder on one message file and prints median nanoseconds per operation for the rows that match
// the .NET MIME rows: parse (whole message to a tree), headers (parse plus Subject, From, To, Date, Message-ID),
// bodies (parse plus every body part's decoded contents) and build (headers, a text part and a 1 MB attachment, written to memory).
// Usage: mimeparse FILE [ITERATIONS]
use mail_builder::MessageBuilder;
use mail_parser::{HeaderValue, MessageParser};
use std::hint::black_box;
use std::time::Instant;

fn median(mut v: Vec<f64>) -> f64 {
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    v[v.len() / 2]
}

fn time<F: FnMut() -> usize>(iters: usize, mut f: F) -> f64 {
    for _ in 0..3 {
        black_box(f());
    }
    let reps = 7;
    let mut samples = Vec::new();
    for _ in 0..reps {
        let t = Instant::now();
        let mut s = 0;
        for _ in 0..iters {
            s += f();
        }
        black_box(s);
        samples.push(t.elapsed().as_nanos() as f64 / iters as f64);
    }
    median(samples)
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let data = std::fs::read(&args[1]).unwrap();
    let iters: usize = args.get(2).map(|s| s.parse().unwrap()).unwrap_or(2000);
    // Large messages need fewer iterations to stay within a second per sample.
    let iters = if data.len() > 1_000_000 { 20.min(iters) } else { iters };
    let parser = MessageParser::default();

    let parse = time(iters, || parser.parse(black_box(&data[..])).map(|m| m.parts().count()).unwrap_or(0));
    let headers = time(iters, || {
        let m = parser.parse(black_box(&data[..])).unwrap();
        let mut n = m.subject().map(|s| s.len()).unwrap_or(0);
        for h in [m.from(), m.to()] {
            if let Some(list) = h {
                for x in list.mailboxes() {
                    n += x.address().map(|s| s.len()).unwrap_or(0) + x.name().map(|s| s.len()).unwrap_or(0);
                }
            }
        }
        if let Some(d) = m.date() {
            n += d.to_timestamp() as usize & 1;
        }
        n + m.message_id().map(|s| s.len()).unwrap_or(0)
    });
    let bodies = time(iters, || {
        let m = parser.parse(black_box(&data[..])).unwrap();
        let mut n = 0;
        for p in m.parts() {
            n += p.decoded().len();
        }
        n
    });
    // Keep HeaderValue in the import list used by older crate versions' prelude; no behaviour.
    let _ = std::mem::size_of::<HeaderValue>();

    let attachment = vec![0x5au8; 1 << 20];
    let build = time(50, || {
        MessageBuilder::new()
            .from(("Alice Example", "alice@example.org"))
            .to(("Bob", "bob@example.net"))
            .subject("Quarterly figures")
            .text_body("See the attachment.")
            .attachment("application/octet-stream", "data.bin", black_box(&attachment[..]))
            .write_to_vec()
            .unwrap()
            .len()
    });
    println!("parse\t{parse:.0}\nheaders\t{headers:.0}\nbodies\t{bodies:.0}\nbuild\t{build:.0}");
}
