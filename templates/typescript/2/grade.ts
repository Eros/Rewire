const scores: number[] = [72, 88, 95];
for (const score of scores) {
  if (score >= 90) {
    console.log("A");
  } else if (score >= 80) {
    console.log("B");
  } else {
    console.log("C");
  }
}
