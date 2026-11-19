import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("smp_probe", Path(__file__).resolve().parents[1] / "test-smp-probe.py")
smp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smp)


def sample(count=4, workers=3, speedup=2800, skip=(), fail=None):
    lines = [f"SMP_PROBE INFO cpus count={count} online={count} workers={workers}"]
    for step in smp.STEPS:
        if step == fail:
            lines.append(f"SMP_PROBE {step} FAIL verloren")
        elif step in skip:
            lines.append(f"SMP_PROBE {step} SKIP cpus={count}")
        else:
            lines.append(f"SMP_PROBE {step} PASS")
        if step == "parallel" and step not in skip:
            lines.append(f"SMP_PROBE INFO parallel speedup_permille={speedup}")
    lines.append("SMP_PROBE PASS")
    return "\n".join(lines) + "\n"


class SmpProbeTests(unittest.TestCase):
    def test_pass_requires_all_steps_and_terminal(self):
        log = "\n".join(f"SMP_PROBE {step} PASS" for step in smp.STEPS) + "\nSMP_PROBE PASS\n"
        self.assertEqual("PASS", smp.classify(log))
        self.assertEqual("RUNNING", smp.classify(log.replace("SMP_PROBE PASS\n", "")))

    def test_failure_takes_priority_over_pass(self):
        self.assertEqual("FAIL", smp.classify("SMP_PROBE locks FAIL lost\nSMP_PROBE PASS\n"))

    def test_duplicate_or_conflicting_step_fails(self):
        self.assertEqual("FAIL", smp.classify("SMP_PROBE gc PASS\nSMP_PROBE gc PASS\n"))

    def test_pass_run(self):
        self.assertEqual([], smp.evaluate(sample(), "kvm", "4"))

    def test_failed_step(self):
        self.assertTrue(smp.evaluate(sample(fail="locks"), "kvm", "4"))

    def test_wrong_cpu_count(self):
        self.assertTrue(smp.evaluate(sample(count=2), "kvm", "4,cores=2,threads=2"))

    def test_single_cpu_skips_and_passes(self):
        self.assertEqual([], smp.evaluate(sample(1, 1, skip=("parallel", "migrate")), "kvm", ""))

    def test_skip_with_two_workers_fails(self):
        self.assertTrue(smp.evaluate(sample(skip=("migrate",)), "kvm", "4"))
        self.assertTrue(smp.evaluate(sample(skip=("migrate",)), "tcg", "4"))

    def test_skip_boundary_two_workers(self):
        # Grenzfall: genau zwei Worker-CPUs (3 CPUs) -> SKIP ist FAIL; ein Worker (2 CPUs) -> SKIP bleibt erlaubt.
        self.assertTrue(smp.evaluate(sample(3, 2, skip=("migrate",)), "kvm", "3"))
        self.assertEqual([], smp.evaluate(sample(2, 1, skip=("parallel", "migrate")), "tcg", "2"))

    def test_speedup_only_judged_on_kvm(self):
        self.assertTrue(smp.evaluate(sample(speedup=900), "kvm", "4"))
        self.assertEqual([], smp.evaluate(sample(speedup=900), "tcg", "4"))

    def test_expected_cpus(self):
        self.assertEqual(8, smp.expected_cpus("8,cores=4,threads=2"))
        self.assertEqual(1, smp.expected_cpus(""))


def panic_log(ticks=8, tick_cpu=3, fault_cpu=0, after=0, halted=True, wrong_cpu=False):
    lines = ["SMP_PROBE INFO cpus count=4 online=4 workers=3", "SMP_PROBE cpus PASS"]
    lines += [f"SMP_PROBE tick {n} cpu={1 if wrong_cpu and n == 2 else tick_cpu}" for n in range(1, ticks + 1)]
    lines.append(f"SMP_PROBE panic fault cpu={fault_cpu} tick_cpu={tick_cpu}")
    lines.append("ZehOS: Thread 9, Wasm-Worker -1, Gastcode 0")
    if halted:
        lines += ["CPU EXCEPTION: Page Fault", "System halted."]
    lines += [f"SMP_PROBE tick {ticks + n} cpu={tick_cpu}" for n in range(1, after + 1)]
    return "\n".join(lines) + "\n"


class SmpProbePanicTests(unittest.TestCase):
    def test_pass(self):
        self.assertEqual([], smp.evaluate_panic(panic_log()))

    def test_tick_after_halt_fails(self):
        self.assertTrue(smp.evaluate_panic(panic_log(after=1)))

    def test_tick_between_fault_and_halt_is_allowed(self):
        # Der Tickthread darf bis zum NMI noch schreiben; erst nach "System halted." muss Ruhe sein.
        log = panic_log().replace("System halted.", "SMP_PROBE tick 9 cpu=3\nSystem halted.")
        self.assertEqual([], smp.evaluate_panic(log))

    def test_missing_halt_fails(self):
        self.assertTrue(smp.evaluate_panic(panic_log(halted=False)))

    def test_same_cpu_fails(self):
        self.assertTrue(smp.evaluate_panic(panic_log(tick_cpu=0, fault_cpu=0)))

    def test_too_few_ticks_fails(self):
        self.assertTrue(smp.evaluate_panic(panic_log(ticks=smp.MIN_TICKS - 1)))

    def test_tick_from_other_cpu_fails(self):
        self.assertTrue(smp.evaluate_panic(panic_log(wrong_cpu=True)))

    def test_probe_fail_line_fails(self):
        self.assertTrue(smp.evaluate_panic("SMP_PROBE panic FAIL nur 1 CPU\n"))


if __name__ == "__main__":
    unittest.main()
