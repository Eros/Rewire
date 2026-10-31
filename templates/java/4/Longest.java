List<String> words = List.of("keyboard", "thumb", "column", "remap");
String longest = words.stream()
    .filter(word -> word.length() > 4)
    .sorted(Comparator.comparingInt(String::length).reversed())
    .map(String::toUpperCase)
    .findFirst()
    .orElse("none");
Map<String, Integer> lengths = words.stream().collect(Collectors.toMap(w -> w, String::length));
