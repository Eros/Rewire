class Counter {
public:
    int count() const { return count_; }

    void increment(int by = 1) { count_ += by; }

private:
    int count_ = 0;
};
