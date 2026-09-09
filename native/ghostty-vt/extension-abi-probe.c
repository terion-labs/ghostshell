#include <ghostty/vt.h>

#if GHOSTTY_ASURA_EXTENSION_ABI != 1u
#error "Unexpected Asura libghostty-vt extension ABI"
#endif

int main(void) {
  return ghostty_asura_extension_abi() ==
                 GHOSTTY_ASURA_EXTENSION_ABI
             ? 0
             : 1;
}
