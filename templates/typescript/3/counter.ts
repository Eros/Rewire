export class Counter {
  private count = 0;

  get value(): number {
    return this.count;
  }

  increment(by: number = 1): void {
    this.count += by;
  }
}
