// V25: C++ mit libstdc++.so.6 und libgcc_s.so.1 (dynamisch): iostream, Container, Ausnahmen ueber
// Funktionsgrenzen (Unwinder aus libgcc_s), std::thread mit thread_local, statische Objekte.
#include <algorithm>
#include <iostream>
#include <map>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {

struct Global {
    Global() { std::cout << "global ctor" << std::endl; }
    ~Global() { std::cout << "global dtor" << std::endl; }
};

Global global;
thread_local int perThread = 7;

[[noreturn]] void deep(int n)
{
    if (n == 0) {
        throw std::runtime_error("tief geworfen");
    }
    std::string guard(static_cast<size_t>(n), 'g');
    deep(n - 1);
}

}

int main()
{
    std::vector<std::string> words{"zeh", "os", "musl", "ladybird", "c++"};
    std::sort(words.begin(), words.end());
    std::string joined;
    for (const auto &w : words) {
        joined += w + ",";
    }
    std::cout << "sortiert " << joined << std::endl;

    std::map<std::string, int> counts;
    for (char c : std::string("abracadabra")) {
        counts[std::string(1, c)]++;
    }
    for (const auto &[k, v] : counts) {
        std::cout << k << "=" << v << ";";
    }
    std::cout << std::endl;

    try {
        deep(20);
    } catch (const std::exception &e) {
        std::cout << "gefangen: " << e.what() << std::endl;
    }

    try {
        std::vector<int> v(3);
        (void)v.at(10);
    } catch (const std::out_of_range &) {
        std::cout << "out_of_range ok" << std::endl;
    }

    auto shared = std::make_shared<int>(41);
    int seen = 0;
    std::thread t([&] {
        perThread += 1;
        seen = perThread + *shared;
    });
    t.join();
    std::cout << "thread " << seen << " haupt " << perThread << std::endl;
    return 0;
}
