/* Parses an iCalendar file with the distribution's libical library, repeatedly, and prints the median parse time.
 * A native reference for the calendar rows (not a gate). The two functions are libical's public C API, declared here so the
 * program builds against the prebuilt runtime package without its development headers:
 *   gcc -O2 libical_parse.c -o libical_parse -l:libical.so.3
 * Usage: libical_parse FILE [ITERATIONS]   prints: median_ns */
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

typedef struct icalcomponent_impl icalcomponent;
extern icalcomponent *icalparser_parse_string(const char *str);
extern void icalcomponent_free(icalcomponent *c);

static int cmp(const void *a, const void *b) {
  double x = *(const double *)a, y = *(const double *)b;
  return (x > y) - (x < y);
}

int main(int argc, char **argv) {
  if (argc < 2) return 2;
  int iters = argc > 2 ? atoi(argv[2]) : 30;
  FILE *f = fopen(argv[1], "rb");
  if (!f) return 1;
  fseek(f, 0, SEEK_END);
  long n = ftell(f);
  fseek(f, 0, SEEK_SET);
  char *buf = malloc((size_t)n + 1);
  if (fread(buf, 1, (size_t)n, f) != (size_t)n) return 1;
  buf[n] = 0;
  fclose(f);
  double *t = malloc(sizeof(double) * (size_t)iters);
  for (int w = 0; w < 3; w++) icalcomponent_free(icalparser_parse_string(buf));
  for (int i = 0; i < iters; i++) {
    struct timespec a, b;
    clock_gettime(CLOCK_MONOTONIC, &a);
    icalcomponent *c = icalparser_parse_string(buf);
    clock_gettime(CLOCK_MONOTONIC, &b);
    if (!c) return 3;
    icalcomponent_free(c);
    t[i] = (double)(b.tv_sec - a.tv_sec) * 1e9 + (double)(b.tv_nsec - a.tv_nsec);
  }
  qsort(t, (size_t)iters, sizeof(double), cmp);
  printf("%.0f\n", t[iters / 2]);
  return 0;
}
