import sys
path = sys.argv[1]
data = open(path, "rb").read()
for n in sys.argv[2:]:
    print(f"{n:40s} {data.count(n.encode())}")
