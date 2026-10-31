type Counter struct {
	count int
}

func (c *Counter) Count() int {
	return c.count
}

func (c *Counter) Increment(by int) {
	c.count += by
}
