export class Counter {
  #count = 0;

  get count() {
    return this.#count;
  }

  increment(by = 1) {
    this.#count += by;
  }
}
