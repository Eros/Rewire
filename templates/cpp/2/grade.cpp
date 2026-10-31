std::vector<int> scores = {72, 88, 95};
for (int score : scores) {
    if (score >= 90) {
        std::cout << "A\n";
    } else if (score >= 80) {
        std::cout << "B\n";
    } else {
        std::cout << "C\n";
    }
}
