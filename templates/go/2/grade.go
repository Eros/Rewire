scores := []int{72, 88, 95}
for _, score := range scores {
	if score >= 90 {
		fmt.Println("A")
	} else if score >= 80 {
		fmt.Println("B")
	} else {
		fmt.Println("C")
	}
}
