class Counter:
    def __init__(self):
        self._count = 0

    @property
    def count(self):
        return self._count

    def increment(self, by=1):
        self._count += by
