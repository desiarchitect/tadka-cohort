# CRDT G-Counter Toy

**Day 15 curveball:** Google Docs / Figma conflict resolution without locks.

## Break (naive merge loses updates)

```bash
cd tadka/toydemo/day-15-breadth/crdt-counter-toy
node index.js --mode=break
```

Expect: merged value = max(7,7) = 7, not 14. Lost updates = 7.

## Fix (G-Counter)

```bash
node index.js --mode=fix
```

Expect: merged total = 14.

## Two-terminal replica demo (optional)

```bash
node index.js --mode=replica --replica=A
node index.js --mode=replica --replica=B
```

Merge JSON states with max-per-slot then sum.