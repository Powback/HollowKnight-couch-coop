#!/usr/bin/env python3
"""Virtual Xbox-360-style gamepad, command-driven over a FIFO.

Commands (one per line on /tmp/hkpad.fifo):
  press <BTN> [seconds]     tap a button (default 0.09s)
  hold <BTN> <seconds>      hold then release
  down <BTN> / up <BTN>     raw state
  axis <ABS> <value>        set axis (-32768..32767; triggers 0..255)
  stick <x> <y>             left stick shorthand
  neutral                   all released/centered
  quit
Buttons: A B X Y START SELECT TL TR THUMBL THUMBR DU DD DL DR (dpad as hat)
"""
import os, sys, time, threading
from evdev import UInput, AbsInfo, ecodes as e

BTN = {'A': e.BTN_SOUTH, 'B': e.BTN_EAST, 'X': e.BTN_NORTH, 'Y': e.BTN_WEST,
       'START': e.BTN_START, 'SELECT': e.BTN_SELECT,
       'TL': e.BTN_TL, 'TR': e.BTN_TR, 'THUMBL': e.BTN_THUMBL, 'THUMBR': e.BTN_THUMBR}
HAT = {'DU': (e.ABS_HAT0Y, -1), 'DD': (e.ABS_HAT0Y, 1),
       'DL': (e.ABS_HAT0X, -1), 'DR': (e.ABS_HAT0X, 1)}
AX = {'ABS_X': e.ABS_X, 'ABS_Y': e.ABS_Y, 'ABS_RX': e.ABS_RX, 'ABS_RY': e.ABS_RY,
      'ABS_Z': e.ABS_Z, 'ABS_RZ': e.ABS_RZ}

stick = AbsInfo(0, -32768, 32767, 16, 128, 0)
trig = AbsInfo(0, 0, 255, 0, 0, 0)
hat = AbsInfo(0, -1, 1, 0, 0, 0)
caps = {
    e.EV_KEY: list(BTN.values()),
    e.EV_ABS: [(e.ABS_X, stick), (e.ABS_Y, stick), (e.ABS_RX, stick), (e.ABS_RY, stick),
               (e.ABS_Z, trig), (e.ABS_RZ, trig), (e.ABS_HAT0X, hat), (e.ABS_HAT0Y, hat)],
}
ui = UInput(caps, name='HKTestPad (Xbox 360 compatible)', vendor=0x045e, product=0x028e, version=0x110)
print('pad created', flush=True)

def syn(): ui.syn()
def key(code, v): ui.write(e.EV_KEY, code, v); syn()
def absw(code, v): ui.write(e.EV_ABS, code, v); syn()

def do(line):
    p = line.strip().split()
    if not p: return True
    c = p[0].lower()
    if c == 'quit': return False
    if c == 'neutral':
        for b in BTN.values(): ui.write(e.EV_KEY, b, 0)
        for a in (e.ABS_X, e.ABS_Y, e.ABS_RX, e.ABS_RY, e.ABS_HAT0X, e.ABS_HAT0Y): ui.write(e.EV_ABS, a, 0)
        for a in (e.ABS_Z, e.ABS_RZ): ui.write(e.EV_ABS, a, 0)
        syn(); return True
    if c in ('press', 'hold', 'down', 'up'):
        n = p[1].upper()
        dur = float(p[2]) if len(p) > 2 else (0.09 if c == 'press' else 0.0)
        if n in HAT:
            code, hv = HAT[n]
            if c == 'up': absw(code, 0)
            elif c == 'down': absw(code, hv)
            else:
                absw(code, hv); time.sleep(dur or 0.09); absw(code, 0)
        elif n in BTN:
            b = BTN[n]
            if c == 'up': key(b, 0)
            elif c == 'down': key(b, 1)
            else:
                key(b, 1); time.sleep(dur or 0.09); key(b, 0)
        return True
    if c == 'axis' and len(p) >= 3:
        absw(AX[p[1].upper()], int(p[2])); return True
    if c == 'stick' and len(p) >= 3:
        ui.write(e.EV_ABS, e.ABS_X, int(p[1])); ui.write(e.EV_ABS, e.ABS_Y, int(p[2])); syn(); return True
    print('?? ' + line, flush=True); return True

fifo = '/tmp/hkpad.fifo'
try: os.unlink(fifo)
except FileNotFoundError: pass
os.mkfifo(fifo)
print('fifo ready', flush=True)
run = True
while run:
    with open(fifo) as f:
        for line in f:
            if not do(line): run = False; break
ui.close()
