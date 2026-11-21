#!/bin/sh
# mss-monitor — history of server load for Multi Server Sync (section "Monitoring").
#
# One long-lived process: every 10 s it reads /proc (cpu, memory, network, disk) and the used
# space on /, folds the samples of a minute into a minute point (average + peak) and closed hours
# into hour points. Plain text files in the data folder, trimmed by the script itself:
#   minute.txt  minute points, last 48 hours
#   hour.txt    hour points, last 180 days
#   minute.cur  the minute in progress (rewritten every sample)
#   rec.txt     all-time records of single samples: key value time
# Point line: t n c cm m mm s rx rxm tx txm rd rdm wr wrm d
#   t - start of the minute/hour (unix time), n - number of 10 s samples inside,
#   c/cm - cpu % avg/peak, m/mm - memory used bytes avg/peak, s - swap used avg,
#   rx tx rd wr - bytes per second avg, *m - peak, d - used bytes on / at the end.
# Formulas are the same as in the program's "Now" view (and rudog sysmon).
# Needs only sh and awk. Remove: systemctl disable --now mss-monitor;
#   rm -rf /usr/local/lib/mss-monitor /var/lib/mss-monitor /etc/systemd/system/mss-monitor.service
#
# For tests: MSS_ROOT - folder used instead of / for /proc and /sys, MSS_DATA - data folder,
# MSS_STEP - seconds between samples, MSS_TICKS - stop after so many samples (0 - never),
# MSS_FAKE_T - start time instead of the clock (no sleeping), MSS_SNAPS - read MSS_ROOT/<i % N>.

DATA=${MSS_DATA:-/var/lib/mss-monitor}
mkdir -p "$DATA" || exit 1

exec awk -v root="${MSS_ROOT:-}" -v data="$DATA" -v step="${MSS_STEP:-10}" \
    -v ticks="${MSS_TICKS:-0}" -v faket="${MSS_FAKE_T:-}" -v snaps="${MSS_SNAPS:-0}" '

function clock(   t) {
    if (faket != "") return faket + tick * step
    srand(); t = srand()
    if (t < 1000000000) { "date +%s" | getline t; close("date +%s") }
    return t + 0
}

# accumulating counters of /proc at the moment of reading
function readraw(base,   f, line, a, n, name, rest, iface, x, i, got) {
    for (i in raw) delete raw[i]
    got = 0

    f = base "/proc/stat"
    while ((getline line < f) > 0) {
        n = split(line, a, " ")
        if (a[1] == "cpu" && n >= 5) {
            # user nice system idle iowait irq softirq steal (guest is inside user)
            raw["total"] = 0
            for (i = 2; i <= 9 && i <= n; i++) raw["total"] += a[i]
            raw["idle"] = a[5] + 0; raw["iow"] = a[6] + 0; raw["stl"] = (n >= 9 ? a[9] : 0) + 0
            got = 1
        }
    }
    close(f)
    if (!got) return 0

    f = base "/proc/meminfo"
    raw["mt"] = 0; raw["ma"] = -1; raw["buf"] = 0; raw["cac"] = 0; raw["srec"] = 0; raw["st"] = 0; raw["sf"] = 0; raw["mf"] = 0
    while ((getline line < f) > 0) {
        split(line, a, " ")
        x = a[2] * 1024
        if (a[1] == "MemTotal:") raw["mt"] = x
        else if (a[1] == "MemAvailable:") raw["ma"] = x
        else if (a[1] == "MemFree:") raw["mf"] = x
        else if (a[1] == "Buffers:") raw["buf"] = x
        else if (a[1] == "Cached:") raw["cac"] = x
        else if (a[1] == "SReclaimable:") raw["srec"] = x
        else if (a[1] == "SwapTotal:") raw["st"] = x
        else if (a[1] == "SwapFree:") raw["sf"] = x
    }
    close(f)
    # old kernels without MemAvailable: free + buffers + page cache
    if (raw["ma"] < 0) raw["ma"] = raw["mf"] + raw["buf"] + raw["cac"]

    # interface of the default route: its traffic is the traffic of the server
    iface = ""
    f = base "/proc/net/route"
    while ((getline line < f) > 0) {
        split(line, a, " ")
        if (iface == "" && a[2] == "00000000") iface = a[1]
    }
    close(f)
    raw["iface"] = iface

    raw["rx"] = 0; raw["tx"] = 0
    f = base "/proc/net/dev"
    while ((getline line < f) > 0) {
        i = index(line, ":")
        if (i == 0) continue
        name = substr(line, 1, i - 1); gsub(/[ \t]/, "", name)
        rest = substr(line, i + 1)
        if (iface != "" && name != iface) continue
        if (iface == "" && name == "lo") continue
        n = split(rest, a, " ")
        if (n < 9) continue
        raw["rx"] += a[1]; raw["tx"] += a[9]
    }
    close(f)

    # sectors of whole disks: no partitions, loop, zram, dm-*, md - otherwise bytes are counted twice
    raw["rd"] = 0; raw["wr"] = 0
    f = base "/proc/diskstats"
    while ((getline line < f) > 0) {
        n = split(line, a, " ")
        if (n < 10) continue
        name = a[3]
        if (name ~ /^(loop|ram|zram|dm-|sr|fd|md)/) continue
        x = base "/sys/block/" name "/dev"
        if ((getline rest < x) <= 0) { close(x); continue }
        close(x)
        raw["rd"] += a[6]; raw["wr"] += a[10]
    }
    close(f)

    # used and total on the root file system, as df: total - free (root reserve counts as used)
    raw["du"] = 0
    x = "df -Pk \"" (base == "" ? "/" : base) "\" 2>/dev/null"
    while ((x | getline line) > 0) {
        n = split(line, a, " ")
        if (n >= 4 && a[2] ~ /^[0-9]+$/) raw["du"] = a[3] * 1024
    }
    close(x)
    return 1
}

function d(b, a) { return b < a ? 0 : b - a }
function pct(v) { return v < 0 ? 0 : (v > 100 ? 100 : v) }

# sample from two readings: rates are deltas per second
function sample(t,   dt, sec) {
    S["t"] = t
    S["cpu"] = 0
    dt = d(raw["total"], prev["total"])
    if (dt > 0) S["cpu"] = pct(100 * (dt - d(raw["idle"], prev["idle"]) - d(raw["iow"], prev["iow"])) / dt)
    S["mem"] = raw["mt"] - raw["ma"]; if (S["mem"] < 0) S["mem"] = 0
    S["swap"] = raw["st"] - raw["sf"]; if (S["swap"] < 0) S["swap"] = 0
    S["rx"] = 0; S["tx"] = 0; S["rd"] = 0; S["wr"] = 0
    sec = t - prev["t"]
    if (sec > 0) {
        # the default interface changed - counters cannot be compared
        if (raw["iface"] == prev["iface"]) { S["rx"] = d(raw["rx"], prev["rx"]) / sec; S["tx"] = d(raw["tx"], prev["tx"]) / sec }
        S["rd"] = d(raw["rd"], prev["rd"]) * 512 / sec
        S["wr"] = d(raw["wr"], prev["wr"]) * 512 / sec
    }
    S["disk"] = raw["du"]
}

function keepraw(t,   k) { for (k in raw) prev[k] = raw[k]; prev["t"] = t }

function mx(a, b) { return a > b ? a : b }

# samples of the current minute - sums and peaks
function addminute() {
    if (M["n"] == 0) { M["t0"] = S["t"]; M["c"] = 0; M["cm"] = 0; M["m"] = 0; M["mm"] = 0; M["s"] = 0
        M["rx"] = 0; M["rxm"] = 0; M["tx"] = 0; M["txm"] = 0; M["rd"] = 0; M["rdm"] = 0; M["wr"] = 0; M["wrm"] = 0 }
    M["n"]++
    M["c"] += S["cpu"]; M["cm"] = mx(M["cm"], S["cpu"])
    M["m"] += S["mem"]; M["mm"] = mx(M["mm"], S["mem"])
    M["s"] += S["swap"]
    M["rx"] += S["rx"]; M["rxm"] = mx(M["rxm"], S["rx"])
    M["tx"] += S["tx"]; M["txm"] = mx(M["txm"], S["tx"])
    M["rd"] += S["rd"]; M["rdm"] = mx(M["rdm"], S["rd"])
    M["wr"] += S["wr"]; M["wrm"] = mx(M["wrm"], S["wr"])
    M["d"] = S["disk"]
}

function minuteline(   n) {
    n = M["n"]
    return sprintf("%d %d %.2f %.2f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f",
        int(M["t0"] / 60) * 60, n, M["c"] / n, M["cm"], M["m"] / n, M["mm"], M["s"] / n,
        M["rx"] / n, M["rxm"], M["tx"] / n, M["txm"], M["rd"] / n, M["rdm"], M["wr"] / n, M["wrm"], M["d"])
}

# hour points for all closed hours after the last written one (survives restarts in the middle of an hour)
function catchup(now,   f, line, a, lastH, curH, h, bh, n) {
    lastH = -1
    f = data "/hour.txt"
    while ((getline line < f) > 0) { split(line, a, " "); if (a[1] ~ /^[0-9]+$/) lastH = a[1] + 0 }
    close(f)

    curH = int(now / 3600) * 3600
    bh = -1
    f = data "/minute.txt"
    while ((getline line < f) > 0) {
        n = split(line, a, " ")
        if (n < 16) continue
        h = int(a[1] / 3600) * 3600
        if (h <= lastH || h >= curH) continue
        if (h != bh) { flushhour(bh); bh = h }
        addpoint(a)
    }
    close(f)
    flushhour(bh)

    # minute points - last 48 hours, hour points - last 180 days
    trim(data "/minute.txt", 2880)
    trim(data "/hour.txt", 4320)
}

function addpoint(a,   w) {
    w = a[2]
    if (H["n"] == 0) { H["c"] = 0; H["cm"] = 0; H["m"] = 0; H["mm"] = 0; H["s"] = 0; H["rx"] = 0; H["rxm"] = 0
        H["tx"] = 0; H["txm"] = 0; H["rd"] = 0; H["rdm"] = 0; H["wr"] = 0; H["wrm"] = 0 }
    H["n"] += w
    H["c"] += a[3] * w; H["cm"] = mx(H["cm"], a[4])
    H["m"] += a[5] * w; H["mm"] = mx(H["mm"], a[6])
    H["s"] += a[7] * w
    H["rx"] += a[8] * w; H["rxm"] = mx(H["rxm"], a[9])
    H["tx"] += a[10] * w; H["txm"] = mx(H["txm"], a[11])
    H["rd"] += a[12] * w; H["rdm"] = mx(H["rdm"], a[13])
    H["wr"] += a[14] * w; H["wrm"] = mx(H["wrm"], a[15])
    H["d"] = a[16]
}

function flushhour(h,   n, f) {
    n = H["n"]
    if (h >= 0 && n > 0) {
        f = data "/hour.txt"
        printf "%d %d %.2f %.2f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f %.0f\n",
            h, n, H["c"] / n, H["cm"], H["m"] / n, H["mm"], H["s"] / n,
            H["rx"] / n, H["rxm"], H["tx"] / n, H["txm"], H["rd"] / n, H["rdm"], H["wr"] / n, H["wrm"], H["d"] >> f
        close(f)
    }
    H["n"] = 0
}

# keeps the last "keep" lines; rewrites only when the file grew noticeably beyond that
function trim(f, keep,   line, n, i, t) {
    n = 0
    while ((getline line < f) > 0) { n++; T[n] = line }
    close(f)
    if (n > keep + 60) {
        t = f ".tmp"
        printf "" > t
        for (i = n - keep + 1; i <= n; i++) print T[i] >> t
        close(t)
        system("mv -f \"" t "\" \"" f "\"")
    }
    for (i = 1; i <= n; i++) delete T[i]
}

function loadrec(   f, line, a) {
    f = data "/rec.txt"
    while ((getline line < f) > 0) { if (split(line, a, " ") >= 3) { RV[a[1]] = a[2] + 0; RTIME[a[1]] = a[3] } }
    close(f)
}

function rec(k, v) { if (v > RV[k] + 0) { RV[k] = v; RTIME[k] = S["t"]; changed = 1 } }

function saverec(   f, k) {
    f = data "/rec.txt"
    printf "" > f
    for (k in RV) printf "%s %.2f %d\n", k, RV[k], RTIME[k] >> f
    close(f)
}

BEGIN {
    step += 0; ticks += 0; snaps += 0
    if (step <= 0) step = 10
    tick = 0
    loadrec()
    catchup(clock())
    M["n"] = 0
    have = 0
    done = 0
    while (ticks == 0 || done < ticks) {
        t = clock()
        base = root
        if (snaps > 0) base = root "/" (tick % snaps)
        if (readraw(base)) {
            if (have) {
                sample(t)
                # the minute changed - the gathered samples become the point of the previous minute
                if (M["n"] > 0 && int(M["t0"] / 60) != int(t / 60)) {
                    line = minuteline()
                    f = data "/minute.txt"
                    print line >> f
                    close(f)
                    hourClosed = int(M["t0"] / 3600) != int(t / 3600)
                    M["n"] = 0
                    if (hourClosed) catchup(t)
                }
                addminute()
                f = data "/minute.cur"
                print minuteline() > f
                close(f)
                changed = 0
                rec("cpu", S["cpu"]); rec("mem", S["mem"]); rec("rx", S["rx"]); rec("tx", S["tx"]); rec("rd", S["rd"]); rec("wr", S["wr"])
                if (changed) saverec()
                done++
            }
            keepraw(t)
            have = 1
        }
        tick++
        if (faket == "") system("sleep " step)
    }
}'
