// Rust mit std (S1): Datei und Zeit. Ausgabe nur deterministisch: Werte sind Wahrheitswerte oder feste Zahlen.
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    println!("args={}", args.len() - 1);
    println!("arg1={} arg2={}", args[1], args[2]);
    println!("env ZEHOS_ABI={:?}", std::env::var("ZEHOS_ABI"));

    // Datei: anlegen, schreiben, zurückspulen, lesen, anhängen, Metadaten, umbenennen, löschen.
    let mut f = File::create("a.txt").unwrap();
    f.write_all(b"hallo rust\n").unwrap();
    drop(f);
    let mut s = String::new();
    File::open("a.txt").unwrap().read_to_string(&mut s).unwrap();
    print!("gelesen={}", s);
    let mut f = OpenOptions::new().append(true).open("a.txt").unwrap();
    f.write_all(b"zweite zeile\n").unwrap();
    drop(f);
    let m = fs::metadata("a.txt").unwrap();
    println!("len={} is_file={} readonly={}", m.len(), m.is_file(), m.permissions().readonly());
    let mut f = File::open("a.txt").unwrap();
    f.seek(SeekFrom::Start(6)).unwrap();
    let mut b = [0u8; 4];
    f.read_exact(&mut b).unwrap();
    println!("seek={}", String::from_utf8_lossy(&b));
    fs::create_dir("d").unwrap();
    fs::rename("a.txt", "d/b.txt").unwrap();
    let names: Vec<_> = fs::read_dir("d").unwrap().map(|e| e.unwrap().file_name().into_string().unwrap()).collect();
    println!("d={:?}", names);
    println!("fehlt={:?}", File::open("fehlt").map(|_| ()).map_err(|e| e.raw_os_error()));
    println!("doppelt={:?}", fs::create_dir("d").map_err(|e| e.raw_os_error()));
    fs::remove_file("d/b.txt").unwrap();
    fs::remove_dir("d").unwrap();
    println!("weg={}", !std::path::Path::new("d").exists());

    // Zeit: monoton, Schlaf mindestens 20 ms, Systemzeit nach 2020 und nicht rückwärts.
    let t0 = Instant::now();
    std::thread::sleep(Duration::from_millis(20));
    let dt = t0.elapsed();
    println!("sleep>=20ms={} <5s={}", dt >= Duration::from_millis(20), dt < Duration::from_secs(5));
    let n = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs();
    println!("realtime>2020={}", n > 1_577_836_800);
    println!("monoton={}", Instant::now() >= t0);
}
