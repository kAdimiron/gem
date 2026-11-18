cmake_minimum_required(VERSION 3.20)
project(tsw LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(LT12_SRC "" CACHE PATH "Path to extracted libtorrent 1.2.20 source directory")
set(LT12_BUILD "" CACHE PATH "Path to compiled libtorrent 1.2.20 build directory containing libtorrent-rasterbar.a")
set(BOOST_ROOT_DIR "" CACHE PATH "Path to Boost headers")
set(OPENSSL_ROOT "" CACHE PATH "Path to OpenSSL root containing include and lib")

add_library(tsw SHARED
    src/library.cpp
    src/events.cpp
    src/settings.cpp
)

target_include_directories(tsw PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${LT12_SRC}/include"
    "${BOOST_ROOT_DIR}"
    "${OPENSSL_ROOT}/include"
)

target_compile_definitions(tsw PRIVATE
    BOOST_ALL_NO_LIB
    BOOST_ASIO_ENABLE_CANCELIO
    BOOST_ASIO_HAS_STD_CHRONO
    BOOST_EXCEPTION_DISABLE
    TORRENT_USE_OPENSSL
    TORRENT_USE_LIBCRYPTO
    WIN32_LEAN_AND_MEAN
    _FILE_OFFSET_BITS=64
    _WIN32_WINNT=0x0600
)

target_compile_options(tsw PRIVATE -O2)

target_link_libraries(tsw PRIVATE
    "${LT12_BUILD}/libtorrent-rasterbar.a"
    "${OPENSSL_ROOT}/lib/libssl.a"
    "${OPENSSL_ROOT}/lib/libcrypto.a"
    ws2_32
    wsock32
    iphlpapi
    crypt32
    bcrypt
    dbghelp
)

target_link_options(tsw PRIVATE
    -static-libgcc
    -static-libstdc++
    -Wl,-Bstatic,--whole-archive,-lwinpthread,--no-whole-archive,-Bdynamic
)

set_target_properties(tsw PROPERTIES
    PREFIX ""
    OUTPUT_NAME "tsw"
)
