/*
    Copyright (c) 2005-2022 Intel Corporation

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

#ifndef _TBB_market_H
#define _TBB_market_H

#include "scheduler_common.h"
#include "market_concurrent_monitor.h"
#include "intrusive_list.h"
#include "rml_tbb.h"
#include "oneapi/tbb/rw_mutex.h"

#include "oneapi/tbb/spin_rw_mutex.h"
#include "oneapi/tbb/task_group.h"
#include "resource_manager.h"

#include <atomic>

#if defined(_MSC_VER) && defined(_Wp64)
    // Workaround for overzealous compiler warnings in /Wp64 mode
    #pragma warning (push)
    #pragma warning (disable: 4244)
#endif

namespace tbb {
namespace detail {

namespace d1 {
class task_scheduler_handle;
}

namespace r1 {

class permit_manager_client;
struct tbb_permit_manager_client;
class task_group_context;
class thread_pool;


//------------------------------------------------------------------------
// Class market
//------------------------------------------------------------------------

class market : public permit_manager, rml::tbb_client {
    friend class task_group_context;
    friend class governor;
    friend class lifetime_control;

public:
    //! Keys for the arena map array. The lower the value the higher priority of the arena list.
    static constexpr unsigned num_priority_levels = 3;

private:
    friend void ITT_DoUnsafeOneTimeInitialization ();
    friend bool finalize_impl(d1::task_scheduler_handle& handle);

    typedef intrusive_list<tbb_permit_manager_client> arena_list_type;
    typedef intrusive_list<thread_data> thread_data_list_type;

    thread_pool* my_thread_pool;

    //! Currently active global market
    static market* theMarket;

    typedef scheduler_mutex_type global_market_mutex_type;

    //! Mutex guarding creation/destruction of theMarket, insertions/deletions in my_arenas, and cancellation propagation
    static global_market_mutex_type  theMarketMutex;

    //! Lightweight mutex guarding accounting operations with arenas list
    typedef rw_mutex arenas_list_mutex_type;
    // TODO: introduce fine-grained (per priority list) locking of arenas.
    arenas_list_mutex_type my_arenas_list_mutex;

    //! Pointer to the RML server object that services this TBB instance.
    rml::tbb_server* my_server;

    //! Maximal number of workers allowed for use by the underlying resource manager
    /** It can't be changed after market creation. **/
    unsigned my_num_workers_hard_limit;

    //! Current application-imposed limit on the number of workers (see set_active_num_workers())
    /** It can't be more than my_num_workers_hard_limit. **/
    std::atomic<unsigned> my_num_workers_soft_limit;

    //! Number of workers currently requested from RML
    int my_num_workers_requested;

    //! First unused index of worker
    /** Used to assign indices to the new workers coming from RML, and busy part
        of my_workers array. **/
    std::atomic<unsigned> my_first_unused_worker_idx;

    //! Number of workers that were requested by all arenas on all priority levels
    std::atomic<int> my_total_demand;

    //! Number of workers that were requested by arenas per single priority list item
    int my_priority_level_demand[num_priority_levels];

#if __TBB_ENQUEUE_ENFORCED_CONCURRENCY
    //! How many times mandatory concurrency was requested from the market
    int my_mandatory_num_requested;
#endif

    //! Per priority list of registered arenas
    arena_list_type my_arenas[num_priority_levels];

    //! The first arena to be checked when idle worker seeks for an arena to enter
    /** The check happens in round-robin fashion. **/
    tbb_permit_manager_client* my_next_arena;

    //! ABA prevention marker to assign to newly created arenas
    std::atomic<uintptr_t> my_arenas_aba_epoch;

    //! Reference count controlling market object lifetime
    std::atomic<unsigned> my_ref_count;

    //! Count of external threads attached
    std::atomic<unsigned> my_public_ref_count;

    //! Stack size of worker threads
    std::size_t my_stack_size;

    //! Shutdown mode
    bool my_join_workers;

    //! The value indicating that the soft limit warning is unnecessary
    static const unsigned skip_soft_limit_warning = ~0U;

    //! Either workers soft limit to be reported via runtime_warning() or skip_soft_limit_warning
    std::atomic<unsigned> my_workers_soft_limit_to_report;

    //! Constructor
    market ( unsigned workers_soft_limit, unsigned workers_hard_limit, std::size_t stack_size );

    //! Destroys and deallocates market object created by market::create()
    void destroy ();

    //! Recalculates the number of workers requested from RML and updates the allotment.
    int update_workers_request();

    //! Recalculates the number of workers assigned to each arena in the list.
    /** The actual number of workers servicing a particular arena may temporarily
        deviate from the calculated value. **/
    void update_allotment (unsigned effective_soft_limit) {
        int total_demand = my_total_demand.load(std::memory_order_relaxed);
        if (total_demand) {
            update_allotment(my_arenas, total_demand, (int)effective_soft_limit);
        }
    }

    template <typename Pred>
    static void enforce (Pred pred, const char* msg) {
        suppress_unused_warning(pred, msg);
#if TBB_USE_ASSERT
        global_market_mutex_type::scoped_lock lock(theMarketMutex);
        __TBB_ASSERT(pred(), msg);
#endif
    }

    ////////////////////////////////////////////////////////////////////////////////
    // Helpers to unify code branches dependent on priority feature presence

    tbb_permit_manager_client* select_next_arena(tbb_permit_manager_client* hint );

    void insert_arena_into_list (tbb_permit_manager_client& a );

    void remove_arena_from_list (tbb_permit_manager_client& a );

    int update_allotment ( arena_list_type* arenas, int total_demand, int max_workers );

    ////////////////////////////////////////////////////////////////////////////////
    // Implementation of rml::tbb_client interface methods

    version_type version () const override { return 0; }

    unsigned max_job_count () const override { return my_num_workers_hard_limit; }

    std::size_t min_stack_size () const override { return worker_stack_size(); }

    job* create_one_job () override;

    void cleanup( job& j ) override;

    void acknowledge_close_connection () override;

    void process( job& j ) override;

public:
    permit_manager_client* create_client(arena& a, constraits_type* constraits) override;
    void destroy_client(permit_manager_client& c) override;

    void request_demand(unsigned min, unsigned max, permit_manager_client&) override;
    void release_demand(permit_manager_client&) override;

    //! Add reference to market if theMarket exists
    static bool add_ref_unsafe( global_market_mutex_type::scoped_lock& lock, bool is_public, unsigned max_num_workers = 0, std::size_t stack_size = 0 );

    static market& global_market(bool is_public, unsigned workers_requested = 0, std::size_t stack_size = 0);

    //! Removes the arena from the market's list
    bool try_destroy_arena (permit_manager_client*, uintptr_t aba_epoch, unsigned priority_level ) override;

    //! Removes the arena from the market's list
    void detach_arena (tbb_permit_manager_client& );

    //! Decrements market's refcount and destroys it in the end
    bool release ( bool is_public, bool blocking_terminate ) override;

#if __TBB_ENQUEUE_ENFORCED_CONCURRENCY
    //! Imlpementation of mandatory concurrency enabling
    void enable_mandatory_concurrency_impl (tbb_permit_manager_client*a );

    bool is_global_concurrency_disabled(permit_manager_client*c );

    //! Inform the external thread that there is an arena with mandatory concurrency
    void enable_mandatory_concurrency (permit_manager_client*c ) override;

    //! Inform the external thread that the arena is no more interested in mandatory concurrency
    void disable_mandatory_concurrency_impl(tbb_permit_manager_client* a);

    //! Inform the external thread that the arena is no more interested in mandatory concurrency
    void mandatory_concurrency_disable (permit_manager_client*c ) override;
#endif /* __TBB_ENQUEUE_ENFORCED_CONCURRENCY */

    //! Request that arena's need in workers should be adjusted.
    /** Concurrent invocations are possible only on behalf of different arenas. **/
    void adjust_demand (permit_manager_client&, int delta, bool mandatory ) override;

    //! Used when RML asks for join mode during workers termination.
    bool must_join_workers () const { return my_join_workers; }

    //! Returns the requested stack size of worker threads.
    std::size_t worker_stack_size() const override { return my_stack_size; }

    //! Set number of active workers
    static void set_active_num_workers( unsigned w );

    //! Reports active parallelism level according to user's settings
    static unsigned app_parallelism_limit();

    //! Reports if any active global lifetime references are present
    static unsigned is_lifetime_control_present();

    //! Finds all contexts affected by the state change and propagates the new state to them.
    /** The propagation is relayed to the market because tasks created by one
        external thread can be passed to and executed by other external threads. This means
        that context trees can span several arenas at once and thus state change
        propagation cannot be generally localized to one arena only. **/
    bool propagate_task_group_state (std::atomic<uint32_t> d1::task_group_context::*mptr_state, d1::task_group_context& src, uint32_t new_state ) override;

    //! List of registered external threads
    thread_data_list_type my_masters;

    //! Array of pointers to the registered workers
    /** Used by cancellation propagation mechanism.
        Must be the last data member of the class market. **/
    std::atomic<thread_data*> my_workers[1];

    static unsigned max_num_workers() {
        global_market_mutex_type::scoped_lock lock( theMarketMutex );
        return theMarket? theMarket->my_num_workers_hard_limit : 0;
    }

    void add_external_thread(thread_data& td) override;

    void remove_external_thread(thread_data& td) override;

    uintptr_t aba_epoch() override {
        return my_arenas_aba_epoch.load(std::memory_order_relaxed);
    }
}; // class market

} // namespace r1
} // namespace detail
} // namespace tbb

#if defined(_MSC_VER) && defined(_Wp64)
    // Workaround for overzealous compiler warnings in /Wp64 mode
    #pragma warning (pop)
#endif // warning 4244 is back

#endif /* _TBB_market_H */
