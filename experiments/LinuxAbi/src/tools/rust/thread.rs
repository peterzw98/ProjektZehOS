// Rust mit std (S3): Threads, Mutex, Kanal, Barriere, Thread-lokale Werte, Panik im Thread (panic=abort wird
// nicht ausgelöst, weil der Thread die Panik nicht auslöst: Rückgabe über join mit Fehlerwert statt Panik).
use std::cell::Cell;
use std::sync::mpsc;
use std::sync::{Arc, Barrier, Mutex};
use std::thread;

thread_local! { static TL: Cell<u32> = Cell::new(7); }

fn main() {
    let sum = Arc::new(Mutex::new(0u64));
    let bar = Arc::new(Barrier::new(4));
    let hs: Vec<_> = (0..4u64)
        .map(|i| {
            let (sum, bar) = (sum.clone(), bar.clone());
            thread::spawn(move || {
                TL.with(|c| c.set(c.get() + i as u32));
                bar.wait();
                for k in 0..1000 { *sum.lock().unwrap() += k * (i + 1); }
                TL.with(|c| c.get())
            })
        })
        .collect();
    let tl: Vec<u32> = hs.into_iter().map(|h| h.join().unwrap()).collect();
    println!("thread_local={:?} haupt={}", tl, TL.with(|c| c.get()));
    println!("summe={}", *sum.lock().unwrap());

    let (tx, rx) = mpsc::channel();
    let ps: Vec<_> = (0..3).map(|i| { let tx = tx.clone(); thread::spawn(move || { for k in 0..5 { tx.send(i * 10 + k).unwrap(); } }) }).collect();
    drop(tx);
    let mut v: Vec<i32> = rx.iter().collect();
    for p in ps { p.join().unwrap(); }
    v.sort();
    println!("kanal={:?}", v);

    let r = thread::Builder::new().name("benannt".into()).stack_size(256 * 1024).spawn(|| thread::current().name().map(String::from)).unwrap().join().unwrap();
    println!("name={:?}", r);
    let sc = thread::scope(|s| { let a = s.spawn(|| 1 + 1); let b = s.spawn(|| 2 * 3); a.join().unwrap() + b.join().unwrap() });
    println!("scope={}", sc);
    println!("cpus>=1={}", thread::available_parallelism().map(|n| n.get() >= 1).unwrap_or(false));
}
